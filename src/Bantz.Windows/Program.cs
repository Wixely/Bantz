using Bantz;
using Bantz.Core;
using Bantz.Capture;
using Bantz.Input;
using Bantz.Platform.Windows;
using Bantz.Settings;
using Bantz.Speech.Whisper;
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

if (args.Contains("--list-inputs", StringComparer.OrdinalIgnoreCase))
{
    // A window application has no console to write to, so the report goes to a file.
    var reportPath = ArgumentValue(args, "--list-inputs") is { Length: > 0 } requested && !requested.StartsWith('-')
        ? Path.GetFullPath(requested)
        : Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "bantz-inputs.txt");
    File.WriteAllText(reportPath, AudioCaptureDevices.Describe());
    Console.WriteLine(reportPath);
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

var modelDownload = ArgumentValue(args, "--download-model");
if (!string.IsNullOrWhiteSpace(modelDownload))
{
    var requested = WhisperModelCatalog.Find(modelDownload);
    if (requested is null)
    {
        Console.Error.WriteLine($"Unknown model '{modelDownload}'. Known models: {string.Join(", ", WhisperModelCatalog.All.Select(entry => entry.Id))}");
        return;
    }

    using var downloadEngine = new WhisperTranscriptionEngine(new WhisperOptions
    {
        ModelsRootProvider = () => storage.ModelsRoot,
        ModelProvider = () => requested,
    });
    var reported = -1;
    await downloadEngine.DownloadModelAsync(requested, new Progress<ModelDownloadProgress>(value =>
    {
        if (value.Percent != reported && value.Percent % 10 == 0)
        {
            reported = value.Percent;
            Console.WriteLine($"{requested.Id}: {value.Percent}%");
        }
    }));
    Console.WriteLine($"{requested.Id} is installed at {downloadEngine.PathFor(requested)}");
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
var inputPreview = args.Contains("--input-preview", StringComparer.OrdinalIgnoreCase);
if (inputPreview)
{
    model.SetInputDevices([
        AudioCaptureDevices.Default,
        new AudioCaptureDevice("0", "Microphone (SteelSeries Arctis 7 Chat)"),
        new AudioCaptureDevice("1", "Microphone (Steam Streaming Microphone)"),
        new AudioCaptureDevice("2", "Webcam C920"),
    ]);
    model.SelectInputDevice("1");
}

var signalAnalyzer = new AudioSignalAnalyzer();
using var recorder = new WindowsAudioRecorder(signalAnalyzer, model.CaptureOptions);
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

var injector = new WindowsTextInjector(() => model.ClipboardPaste);
using var workflow = new DictationWorkflow(
    recorder,
    engine,
    injector,
    new SystemAsyncDelay(),
    () => model.AutoEnter,
    model.DelayFor,
    shouldAutomaticallyWrite: () => model.AutoWrite,
    audioSignalSummary: () => signalAnalyzer.Summary);
var initialWindowSize = WindowsDisplayWorkArea.FitInitialWindow(BantzApp.PreferredWindowSize);
var app = new BantzApp(workflow, model, settingsStore, engine, runtimeManager, storage, signalAnalyzer, initialWindowSize);
if (!inputPreview)
{
    app.RefreshInputDevices();
    app.RefreshInstalledModels();
}
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
    model.RecordingBarOneScale = "0.28";
    model.RecordingBarTwoScale = "0.86";
    model.RecordingBarThreeScale = "0.52";
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

    void RenderFrame(CupriFace.Shell.RenderContext context)
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
    }

    // Clicks are dispatched between two renders: the first gives the document its geometry, so a
    // hit test lands on something, and the second captures what the click did.
    var clicks = ClickPoints(args);
    if (clicks.Count > 0)
    {
        // Clicking exercises handlers that would otherwise persist settings; a snapshot run only
        // renders, so the settings file is left alone.
        settingsStore.Suspend();
        using (renderer.RenderFrames(1, RenderFrame)) { }
        foreach (var (x, y) in clicks)
        {
            var hit = document.HitTest(x, y);
            Console.WriteLine($"click ({x:N0},{y:N0}) hit: {DescribeNode(hit)}");
            // A real pointer moves, presses and releases; a synthetic click alone does not reach
            // components that track press state.
            // The desktop host uses pointer id 0 and only falls back to a click when the pointer
            // press is not handled; mirror that exactly.
            document.DispatchPointerMove(x, y);
            var down = document.DispatchPointer(0, CupriFace.Interaction.PointerPhase.Down, x, y);
            if (!down)
            {
                document.DispatchClick(x, y, 1);
            }

            var up = document.DispatchPointer(0, CupriFace.Interaction.PointerPhase.Up, x, y);
            Console.WriteLine($"  down={down} up={up}");
        }
    }

    var scrollTarget = ArgumentValue(args, "--scroll-path");
    if (!string.IsNullOrWhiteSpace(scrollTarget))
    {
        settingsStore.Suspend();
        using (renderer.RenderFrames(1, RenderFrame)) { }
        var parts = scrollTarget.Split('|');
        var path = parts[0];
        var delta = parts.Length > 1 && float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 120f;
        Console.WriteLine($"ScrollCaptured('{path}', {delta}): {document.ScrollCaptured(path, null, delta, 0)}");
        Console.WriteLine($"Overscroll('{path}', {delta}): {document.Overscroll(path, delta)}");
    }

    var wheels = WheelPoints(args);
    if (wheels.Count > 0)
    {
        settingsStore.Suspend();
        using (renderer.RenderFrames(1, RenderFrame)) { }
        foreach (var (x, y, delta) in wheels)
        {
            Console.WriteLine($"wheel ({x:N0},{y:N0}) delta {delta:N0}: {document.DispatchWheel(x, y, delta)}");
        }
    }

    var drag = ArgumentValue(args, "--drag");
    if (!string.IsNullOrWhiteSpace(drag))
    {
        settingsStore.Suspend();
        using (renderer.RenderFrames(1, RenderFrame)) { }
        var parts = drag.Split(',').Select(part => float.Parse(part, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var (x1, y1, x2, y2) = (parts[0], parts[1], parts[2], parts[3]);
        Console.WriteLine($"drag ({x1:N0},{y1:N0}) -> ({x2:N0},{y2:N0})");
        document.DispatchPointerMove(x1, y1);
        // The host tries the pointer press first and falls back to a click; the mouse path grabs
        // drag surfaces inside that click, so both have to run.
        var byPointer = document.DispatchPointer(0, CupriFace.Interaction.PointerPhase.Down, x1, y1);
        var pressed = byPointer || document.DispatchClick(x1, y1, 1);
        // A user drags an idle window, so wait for startup's redraw requests to lapse first.
        for (var waited = 0;
            args.Contains("--drag-refresh", StringComparer.OrdinalIgnoreCase) && waited < 3000 && app.RefreshIntervalSeconds > 0;
            waited += 50)
        {
            System.Threading.Thread.Sleep(50);
        }

        Console.WriteLine($"  tick interval: {app.RefreshIntervalSeconds:N2}s");
        Console.WriteLine($"  consumed by: {(byPointer ? "DispatchPointer" : pressed ? "DispatchClick" : "nothing")} captured={document.IsPointerCaptured(0)}");

        Console.WriteLine($"  press: {pressed} hit: {DescribeNode(document.HitTest(x1, y1))}");
        ReportScroll(document, "  after press");
        for (var step = 1; step <= 8; step++)
        {
            var t = step / 8f;
            var mx = x1 + (x2 - x1) * t;
            var my = y1 + (y2 - y1) * t;
            if (!document.DispatchPointer(0, CupriFace.Interaction.PointerPhase.Move, mx, my))
            {
                document.DispatchPointerMove(mx, my);
            }

            // Mimics the periodic Refresh the shell runs, which happens only while the app asks
            // for a tick.
            if (args.Contains("--drag-refresh-always", StringComparer.OrdinalIgnoreCase) ||
                (args.Contains("--drag-refresh", StringComparer.OrdinalIgnoreCase) && app.RefreshIntervalSeconds > 0))
            {
                document.Refresh();
            }
        }

        ReportScroll(document, "  after move");
        if (!document.DispatchPointer(0, CupriFace.Interaction.PointerPhase.Up, x2, y2))
        {
            document.DispatchPointerUp(x2, y2);
        }

        ReportScroll(document, "  after release");
    }

    var scrollProbe = ArgumentValue(args, "--probe-scroll");
    if (!string.IsNullOrWhiteSpace(scrollProbe))
    {
        using (renderer.RenderFrames(1, RenderFrame)) { }
        var parts = scrollProbe.Split(',').Select(part => float.Parse(part, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var hit = document.HitTest(parts[0], parts[1]);
        for (var node = hit; node is not null; node = NodeParent(node))
        {
            var type = node.GetType();
            bool Scrollable() => (bool)(type.GetProperty("IsScrollable")?.GetValue(node) ?? false);
            if (!Scrollable())
            {
                continue;
            }

            float Read(string name)
            {
                var member = (object?)type.GetProperty(name)?.GetValue(node) ?? type.GetField(name)?.GetValue(node);
                return member is null ? float.NaN : Convert.ToSingle(member, System.Globalization.CultureInfo.InvariantCulture);
            }

            var element = type.GetField("Element")?.GetValue(node);
            var classes = element?.GetType().GetProperty("ClassList")?.GetValue(element) as System.Collections.IEnumerable;
            var nodeName = classes is null ? "?" : string.Join(".", classes.Cast<object>().Select(c => c?.ToString()));
            var thumbH = MathF.Max(28f, Read("ContentBoxHeight") * Read("ContentBoxHeight") / Read("ScrollContentHeight"));
            // X and Y are relative to the parent, so walk the chain for the painted position.
            float absX = 0, absY = 0;
            for (var walk = node; walk is not null; walk = NodeParent(walk))
            {
                var walkType = walk.GetType();
                absX += Convert.ToSingle(walkType.GetField("X")?.GetValue(walk) ?? 0f, System.Globalization.CultureInfo.InvariantCulture);
                absY += Convert.ToSingle(walkType.GetField("Y")?.GetValue(walk) ?? 0f, System.Globalization.CultureInfo.InvariantCulture);
            }

            var thumbX = absX + Read("Width") - Read("BorderRightW") - 8f;
            Console.WriteLine($"  list at ({absX:N1},{absY:N1}) size {Read("Width"):N1}x{Read("Height"):N1}");
            Console.WriteLine($"  thumb x={thumbX:N1} grab x in [{thumbX - 6:N1}..{thumbX + 13:N1}], y in [{absY:N1}..{absY + thumbH:N1}]");
            Console.WriteLine($"scrollable '{nodeName}': ScrollY={Read("ScrollY"):N1} Max={Read("MaxScrollY"):N1} ContentH={Read("ScrollContentHeight"):N1} BoxH={Read("ContentBoxHeight"):N1} W={Read("Width"):N1}");
            foreach (var name in new[] { "X", "Y", "AbsX", "AbsY", "PaintX", "PaintY", "ContentTopInset" })
            {
                var value = type.GetProperty(name)?.GetValue(node);
                if (value is not null)
                {
                    Console.WriteLine($"  {name}={value}");
                }
            }

            break;
        }
    }

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

static CupriFace.Dom.RenderNode? NodeParent(CupriFace.Dom.RenderNode node) => node.Parent;

static List<(float X, float Y, float Delta)> WheelPoints(string[] values)
{
    var points = new List<(float X, float Y, float Delta)>();
    for (var index = 0; index < values.Length - 1; index++)
    {
        if (!string.Equals(values[index], "--wheel", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var parts = values[index + 1].Split(',');
        if (parts.Length == 3 &&
            float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var x) &&
            float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var y) &&
            float.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var delta))
        {
            points.Add((x, y, delta));
        }
    }

    return points;
}

static List<(float X, float Y)> ClickPoints(string[] values)
{
    var points = new List<(float X, float Y)>();
    for (var index = 0; index < values.Length - 1; index++)
    {
        if (!string.Equals(values[index], "--click", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var parts = values[index + 1].Split(',');
        if (parts.Length == 2 &&
            float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var x) &&
            float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var y))
        {
            points.Add((x, y));
        }
    }

    return points;
}

static void ReportScroll(CupriFace.CupriDocument document, string label)
{
    var root = typeof(CupriFace.CupriDocument)
        .GetField("_root", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?.GetValue(document);
    var found = new List<string>();
    void Walk(object? node)
    {
        if (node is null) return;
        var type = node.GetType();
        if (Convert.ToBoolean(type.GetProperty("IsScrollable")?.GetValue(node) ?? false, System.Globalization.CultureInfo.InvariantCulture))
        {
            var element = type.GetField("Element")?.GetValue(node);
            var classes = element?.GetType().GetProperty("ClassList")?.GetValue(element);
            var name = classes is System.Collections.IEnumerable list
                ? string.Join(".", list.Cast<object>().Select(item => item?.ToString()))
                : "?";
            var scrollY = type.GetField("ScrollY")?.GetValue(node);
            found.Add($"{name}={Convert.ToSingle(scrollY ?? 0f, System.Globalization.CultureInfo.InvariantCulture):N1}");
        }

        if (type.GetField("Children")?.GetValue(node) is System.Collections.IEnumerable children)
        {
            foreach (var child in children) Walk(child);
        }
    }

    Walk(root);
    Console.WriteLine($"{label}: {(found.Count > 0 ? string.Join(" ", found) : "<no scrollers>")}");
}

static string DescribeNode(object? node)
{
    if (node is null)
    {
        return "<nothing>";
    }

    var type = node.GetType();
    var parts = new List<string>();
    foreach (var name in new[] { "Tag", "Id", "ClassName", "Classes", "Text" })
    {
        var value = (object?)type.GetProperty(name)?.GetValue(node) ?? type.GetField(name)?.GetValue(node);
        if (value is string text && text.Length > 0)
        {
            parts.Add($"{name}='{(text.Length > 40 ? text[..40] : text)}'");
        }
        else if (value is System.Collections.IEnumerable items and not string)
        {
            var joined = string.Join(" ", items.Cast<object>().Select(item => item?.ToString()));
            if (joined.Length > 0)
            {
                parts.Add($"{name}='{joined}'");
            }
        }
    }

    return parts.Count > 0 ? string.Join(" ", parts) : type.Name;
}

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
