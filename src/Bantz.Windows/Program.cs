using Bantz.Core;
using Bantz.Platform.Windows;
using Bantz.Settings;
using Bantz.Transcription;
using Bantz.Ui;
using CupriFace.Shell;
using SkiaSharp;

if (!OperatingSystem.IsWindows())
{
    throw new PlatformNotSupportedException("This first Bantz build supports Windows only.");
}

if (args.Length == 3 && string.Equals(args[0], "--build-icon", StringComparison.OrdinalIgnoreCase))
{
    BuildIconAssets(args[1], args[2]);
    return;
}

if (args.Length == 3 && string.Equals(args[0], "--build-disabled-icon", StringComparison.OrdinalIgnoreCase))
{
    File.WriteAllBytes(args[2], ShortcutStateIcon.CreateDisabled(File.ReadAllBytes(args[1])));
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
WhisperTranscriptionEngine.ConfigureRuntime(selectedRuntime, runtimeManager);
var model = new BantzModel(settings);
if (args.Contains("--shortcuts-disabled", StringComparer.OrdinalIgnoreCase))
{
    model.ShortcutsEnabled = false;
}
var signalAnalyzer = new AudioSignalAnalyzer();
using var recorder = new WindowsAudioRecorder(signalAnalyzer);
using var engine = new WhisperTranscriptionEngine(() => modelOverride ?? storage.ModelPath);
if (args.Contains("--probe-runtime", StringComparer.OrdinalIgnoreCase))
{
    engine.ProbeRuntime();
    return;
}

var injector = new WindowsTextInjector();
using var workflow = new DictationWorkflow(
    recorder,
    engine,
    injector,
    new SystemAsyncDelay(),
    () => model.AutoEnter,
    model.DelayFor,
    shouldAutomaticallyWrite: () => model.AutoWrite);
var initialWindowSize = WindowsDisplayWorkArea.FitInitialWindow(BantzApp.PreferredWindowSize);
var app = new BantzApp(workflow, model, settingsStore, engine, runtimeManager, storage, signalAnalyzer, initialWindowSize);
if (!storage.IsSelected)
{
    model.Page = "storage";
}
else if (settings.Runtime is null || !engine.IsModelAvailable || !runtimeManager.IsInstalled(model.SelectedRuntime))
{
    model.Page = "onboarding";
}

var requestedPage = args
    .SkipWhile(value => !string.Equals(value, "--page", StringComparison.OrdinalIgnoreCase))
    .Skip(1)
    .FirstOrDefault();
if (requestedPage is "main" or "settings" or "keybinds" or "diagnostics" or "about" or "onboarding" or "storage")
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

var requestedCountdown = ArgumentValue(args, "--countdown");
if (int.TryParse(requestedCountdown, out var countdown) && countdown is >= 1 and <= 10)
{
    model.Countdown = countdown.ToString(System.Globalization.CultureInfo.InvariantCulture);
    model.CountdownPercent = 100;
    model.StateClass = "targeting";
    model.Status = "Focus the field where Bantz should type.";
}

var recordingPreview = args.Contains("--recording-preview", StringComparer.OrdinalIgnoreCase);
if (recordingPreview)
{
    model.Page = "main";
    model.StateClass = "recording";
    model.RecordLabel = "LISTENING";
    model.RecordHint = "Release when you’re done";
    model.RecordingNotificationDisplay = "flex";
    model.RecordingBarOneScale = "0.28";
    model.RecordingBarTwoScale = "0.86";
    model.RecordingBarThreeScale = "0.52";
    model.RecordingBarFourScale = "0.72";
    model.Status = "Listening… release to transcribe";
}

var snapshotPath = args
    .SkipWhile(value => !string.Equals(value, "--snapshot", StringComparison.OrdinalIgnoreCase))
    .Skip(1)
    .FirstOrDefault();
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

using var input = new WindowsHoldInputMonitor(
    model.GetBindingsSnapshot,
    () => model.ShortcutsEnabled,
    model.GetShortcutToggleBindingSnapshot);
app.BeginBindingCapture = input.BeginCapture;
app.CancelBindingCapture = input.CancelCapture;
input.BindingCaptured += app.BindingCaptured;
input.CaptureCancelled += app.BindingCaptureCancelled;
input.ShortcutTogglePressed += app.ToggleShortcutsEnabled;
using var taskbarIcon = new WindowsTaskbarIconController();
app.ShortcutIconChanged += () => taskbarIcon.SetIdleIcon(app.Icon);
app.RecordingStateChanged += recording =>
{
    if (recording)
    {
        taskbarIcon.StartRecording(app.RecordingIconFrames);
    }
    else
    {
        taskbarIcon.StopRecording(app.Icon);
    }
};
if (recordingPreview)
{
    taskbarIcon.StartRecording(app.RecordingIconFrames);
}
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

static void BuildIconAssets(string sourcePath, string outputDirectory)
{
    Directory.CreateDirectory(outputDirectory);
    using var source = SKBitmap.Decode(sourcePath) ?? throw new InvalidDataException("The icon source is not a readable image.");
    using var icon512 = ResizeWithTransparentCorners(source, 512);
    using var image512 = SKImage.FromBitmap(icon512);
    using var png512 = image512.Encode(SKEncodedImageFormat.Png, 100);
    var pngPath = Path.Combine(outputDirectory, "BantzIcon.png");
    using (var output = File.Create(pngPath)) png512.SaveTo(output);

    using var icon256 = ResizeWithTransparentCorners(source, 256);
    using var image256 = SKImage.FromBitmap(icon256);
    using var png256 = image256.Encode(SKEncodedImageFormat.Png, 100);
    var pngBytes = png256.ToArray();
    var icoPath = Path.Combine(outputDirectory, "Bantz.ico");
    using var iconStream = File.Create(icoPath);
    using var writer = new BinaryWriter(iconStream);
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)1);
    writer.Write((byte)0);
    writer.Write((byte)0);
    writer.Write((byte)0);
    writer.Write((byte)0);
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write((uint)pngBytes.Length);
    writer.Write((uint)22);
    writer.Write(pngBytes);
}

static SKBitmap ResizeWithTransparentCorners(SKBitmap source, int size)
{
    var resized = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul));
    using var sourceImage = SKImage.FromBitmap(source);
    using (var canvas = new SKCanvas(resized))
    {
        canvas.Clear(SKColors.Transparent);
        canvas.DrawImage(
            sourceImage,
            new SKRect(0, 0, size, size),
            new SKSamplingOptions(SKCubicResampler.Mitchell));
    }

    for (var y = 0; y < size; y++)
    {
        for (var x = 0; x < size; x++)
        {
            var color = resized.GetPixel(x, y);
            var lightestCorner = Math.Min(color.Red, Math.Min(color.Green, color.Blue));
            if (lightestCorner > 242)
            {
                resized.SetPixel(x, y, color.WithAlpha(0));
            }
            else if (lightestCorner > 220)
            {
                var alpha = (byte)Math.Clamp((242 - lightestCorner) * 12, 0, 255);
                resized.SetPixel(x, y, color.WithAlpha(alpha));
            }
        }
    }

    return resized;
}
