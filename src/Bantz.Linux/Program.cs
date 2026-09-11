using Bantz;
using Bantz.Core;
using Bantz.Input;
using Bantz.Capture;
using Bantz.Platform.Linux;
using Bantz.Settings;
using Bantz.Speech.Whisper;
using Bantz.Ui;
using CupriFace.Shell;
using SkiaSharp;

if (!OperatingSystem.IsLinux())
{
    throw new PlatformNotSupportedException("This Bantz build targets Linux only.");
}

// What input hardware this machine has, and whether Bantz may read it. Bantz has no global input
// on Linux, and building one means reading evdev nodes, so this reports what such a path would
// find on a machine that cannot be attached to a debugger.
if (args.Contains("--list-hid", StringComparer.OrdinalIgnoreCase))
{
    Console.Write(Bantz.Input.LinuxInputDevices.Describe());
    return;
}

var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
var storage = new AppStorage(executableDirectory);
var settingsStore = new SettingsStore(() => storage.SettingsPath);
var settings = storage.IsSelected ? settingsStore.Load() : AppSettings.Defaults();
var modelOverride = ModelPath.Override(args);
var runtimeRoot = ArgumentValue(args, "--runtime-root");
var runtimeManager = new WhisperRuntimeManager(() => runtimeRoot ?? storage.RuntimeRoot);
var runtimeDownload = ArgumentValue(args, "--download-runtime");
if (Enum.TryParse<TranscriptionRuntime>(runtimeDownload, ignoreCase: true, out var downloadRuntime))
{
    await runtimeManager.DownloadAsync(downloadRuntime, progress: null);
    return;
}

var runtimeOverride = ArgumentValue(args, "--runtime");
var hasRuntimeOverride = Enum.TryParse<TranscriptionRuntime>(runtimeOverride, ignoreCase: true, out var parsedRuntime);
var selectedRuntime = hasRuntimeOverride
    ? parsedRuntime
    : settings.Runtime ?? TranscriptionRuntime.Automatic;
if (hasRuntimeOverride)
{
    settings.Runtime = selectedRuntime;
}

var model = new BantzModel(settings);
if (args.Contains("--shortcuts-disabled", StringComparer.OrdinalIgnoreCase))
{
    model.ShortcutsEnabled = false;
}
var signalAnalyzer = new AudioSignalAnalyzer();
using var recorder = new LinuxAudioRecorder(signalAnalyzer, model.CaptureOptions);
// A --model path pins one file; otherwise the chosen catalogue model decides the file, so that
// switching models in the Models tab applies to the next transcription.
using var engine = new WhisperTranscriptionEngine(modelOverride is null
    ? new WhisperOptions
    {
        ModelsRootProvider = () => storage.ModelsRoot,
        ModelProvider = () => model.SelectedModel,
        LanguageProvider = () => model.EffectiveLanguage,
        RuntimeRootProvider = () => runtimeRoot ?? storage.RuntimeRoot,
        Runtime = selectedRuntime,
    }
    : new WhisperOptions
    {
        ModelPathProvider = () => modelOverride,
        LanguageProvider = () => model.EffectiveLanguage,
        RuntimeRootProvider = () => runtimeRoot ?? storage.RuntimeRoot,
        Runtime = selectedRuntime,
    });
if (args.Contains("--probe-runtime", StringComparer.OrdinalIgnoreCase))
{
    engine.ProbeRuntime();
    return;
}

var injector = new LinuxTextInjector(() => model.ClipboardPaste);
using var workflow = new DictationWorkflow(
    recorder,
    engine,
    injector,
    new SystemAsyncDelay(),
    () => model.AutoEnter,
    model.DelayFor,
    shouldAutomaticallyWrite: () => model.AutoWrite,
    audioSignalSummary: () => signalAnalyzer.Summary);
var initialWindowSize = LinuxDisplayWorkArea.FitInitialWindow(BantzApp.PreferredWindowSize);
var app = new BantzApp(workflow, model, settingsStore, engine, runtimeManager, storage, signalAnalyzer, initialWindowSize);
app.RefreshInputDevices();
app.RefreshInstalledModels();
if (!storage.IsSelected)
{
    model.Page = "storage";
}
else if (settings.Runtime is null || !engine.IsModelAvailable || !runtimeManager.IsInstalled(model.SelectedRuntime))
{
    model.Page = "onboarding";
}

var requestedPage = ArgumentValue(args, "--page");
if (requestedPage is "main" or "settings" or "input" or "models" or "keybinds" or "diagnostics" or "about" or "onboarding" or "storage")
{
    model.Page = requestedPage;
    model.AdvancedBindingsExpanded = requestedPage == "keybinds" &&
        args.Contains("--advanced", StringComparer.OrdinalIgnoreCase);
    model.ShortcutInfoExpanded = model.AdvancedBindingsExpanded &&
        args.Contains("--shortcut-info", StringComparer.OrdinalIgnoreCase);
    if (requestedPage == "diagnostics")
    {
        app.RefreshDiagnostics();
    }
}

var snapshotPath = ArgumentValue(args, "--snapshot");
if (!string.IsNullOrWhiteSpace(snapshotPath))
{
    var document = app.CreateDocument();
    var snapshotWidth = PositiveArgument(args, "--snapshot-width", app.Width);
    var snapshotHeight = PositiveArgument(args, "--snapshot-height", app.Height);
    var renderer = new HeadlessRenderer(snapshotWidth, snapshotHeight);
    using var image = renderer.RenderFrames(1, context =>
    {
        context.Canvas.Clear(app.Background);
        var presentation = app.Present(context.Width, context.Height);
        context.Canvas.Save();
        if (presentation.Scale != 1f)
        {
            context.Canvas.Scale(presentation.Scale);
        }

        document.Render(context.Canvas, presentation.LogicalWidth, presentation.LogicalHeight);
        context.Canvas.Restore();
    });
    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    var fullSnapshotPath = Path.GetFullPath(snapshotPath);
    Directory.CreateDirectory(Path.GetDirectoryName(fullSnapshotPath)!);
    using var output = File.Create(fullSnapshotPath);
    encoded.SaveTo(output);
    return;
}

var allowMultipleInstances = args.Contains("--allow-multiple-instances", StringComparer.OrdinalIgnoreCase);
using var instanceLock = allowMultipleInstances
    ? null
    : SingleInstanceLock.TryAcquire("Bantz.InteractiveApp");
if (!allowMultipleInstances && instanceLock is null)
{
    return;
}

// Global hold-to-talk, read from the kernel's input devices. Nothing was wired here before, so
// the Keybinds tab could not capture anything at all — which read as "gamepad buttons will not
// bind" but applied equally to the keyboard and the mouse.
using var input = new LinuxHoldInputMonitor(
    model.GetBindingsSnapshot,
    () => model.ShortcutsEnabled,
    model.GetShortcutToggleBindingSnapshot);
app.BeginBindingCapture = input.BeginCapture;
app.CancelBindingCapture = input.CancelCapture;
input.BindingCaptured += app.BindingCaptured;
input.CaptureCancelled += app.BindingCaptureCancelled;
input.ShortcutTogglePressed += app.ToggleShortcutsEnabled;
input.HotkeyPressed += () =>
{
    if (model.Page is not ("onboarding" or "storage"))
    {
        _ = workflow.StartAsync(ActivationKind.Hotkey);
    }
};
input.HotkeyReleased += () =>
{
    if (model.Page is not ("onboarding" or "storage"))
    {
        _ = workflow.StopAsync(ActivationKind.Hotkey);
    }
};

DesktopHost.Run(app);

static string? ArgumentValue(string[] values, string name) => values
    .SkipWhile(value => !string.Equals(value, name, StringComparison.OrdinalIgnoreCase))
    .Skip(1)
    .FirstOrDefault();

static int PositiveArgument(string[] values, string name, int fallback) =>
    int.TryParse(ArgumentValue(values, name), out var parsed) && parsed is >= 320 and <= 4096
        ? parsed
        : fallback;
