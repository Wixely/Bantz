using Bantz.Core;
using Bantz.Platform.Linux;
using Bantz.Settings;
using Bantz.Transcription;
using Bantz.Ui;
using CupriFace.Shell;
using SkiaSharp;

if (!OperatingSystem.IsLinux())
{
    throw new PlatformNotSupportedException("This Bantz build targets Linux only.");
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

WhisperTranscriptionEngine.ConfigureRuntime(selectedRuntime, runtimeManager);
var model = new BantzModel(settings);
using var recorder = new LinuxAudioRecorder();
using var engine = new WhisperTranscriptionEngine(() => modelOverride ?? storage.ModelPath);
if (args.Contains("--probe-runtime", StringComparer.OrdinalIgnoreCase))
{
    engine.ProbeRuntime();
    return;
}

var injector = new LinuxTextInjector();
using var workflow = new DictationWorkflow(
    recorder,
    engine,
    injector,
    new SystemAsyncDelay(),
    () => model.AutoEnter,
    model.DelayFor,
    shouldAutomaticallyWrite: () => model.AutoWrite);
var app = new BantzApp(workflow, model, settingsStore, engine, runtimeManager, storage);
if (!storage.IsSelected)
{
    model.Page = "storage";
}
else if (settings.Runtime is null || !engine.IsModelAvailable || !runtimeManager.IsInstalled(model.SelectedRuntime))
{
    model.Page = "onboarding";
}

var requestedPage = ArgumentValue(args, "--page");
if (requestedPage is "main" or "settings" or "keybinds" or "diagnostics" or "onboarding" or "storage")
{
    model.Page = requestedPage;
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

DesktopHost.Run(app);

static string? ArgumentValue(string[] values, string name) => values
    .SkipWhile(value => !string.Equals(value, name, StringComparison.OrdinalIgnoreCase))
    .Skip(1)
    .FirstOrDefault();

static int PositiveArgument(string[] values, string name, int fallback) =>
    int.TryParse(ArgumentValue(values, name), out var parsed) && parsed is >= 320 and <= 4096
        ? parsed
        : fallback;
