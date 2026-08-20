using Bantz.Core;
using Bantz.Settings;
using Bantz.Transcription;
using CupriFace;
using CupriFace.Binding;
using CupriFace.Resources;
using SkiaSharp;

namespace Bantz.Ui;

public sealed class BantzApp : CupriApp
{
    private const float DesignWidth = 560;
    private const float DesignHeight = 720;
    private readonly DictationWorkflow _workflow;
    private readonly BantzModel _model;
    private readonly SettingsStore _settingsStore;
    private readonly WhisperTranscriptionEngine _engine;
    private readonly WhisperRuntimeManager _runtimeManager;
    private readonly AppStorage _storage;
    private List<InputBinding>? _bindingUndo;
    private bool _modelDownloadInProgress;
    private string _latestTranscript = "";

    public BantzApp(
        DictationWorkflow workflow,
        BantzModel model,
        SettingsStore settingsStore,
        WhisperTranscriptionEngine engine,
        WhisperRuntimeManager runtimeManager,
        AppStorage storage)
    {
        _workflow = workflow;
        _model = model;
        _settingsStore = settingsStore;
        _engine = engine;
        _runtimeManager = runtimeManager;
        _storage = storage;
        _workflow.SnapshotChanged += ApplySnapshot;
        _model.SettingsChanged += SaveSettings;
        UpdateModelSetup();
        ApplySnapshot(_workflow.Snapshot);
    }

    public Func<bool>? BeginBindingCapture { get; set; }
    public Action? CancelBindingCapture { get; set; }

    public override string Title => "Bantz";
    public override int Width => 1170;
    public override int Height => 1300;
    public override SKColor Background => new(0x0d, 0x10, 0x17);
    public override bool DarkWindowChrome => true;
    public override bool TopMost => _model.AlwaysOnTop;
    public override bool CloseToTray => true;
    public override byte[] Icon => EmbeddedAsset("Assets/BantzIcon.png").ReadBytes();
    public override object Model => _model;
    public override double RefreshIntervalSeconds => 0.1;
    protected override CupriSource MarkupSource => Assets.Bantz.Html;
    protected override CupriSource StyleSource => Assets.Bantz.Css;

    public override PresentInfo Present(float windowWidth, float windowHeight)
    {
        var zoom = Math.Clamp(MathF.Min(windowWidth / DesignWidth, windowHeight / DesignHeight), 0.65f, 2f);
        return new PresentInfo(windowWidth / zoom, windowHeight / zoom, zoom);
    }

    public override void Configure(CupriDocument document)
    {
        document.OnClick(".storage-portable", _ => SelectStorage(StorageMode.Portable));
        document.OnClick(".storage-user", _ => SelectStorage(StorageMode.PerUser));
        document.OnPointer("data-ptt", pointerEvent =>
        {
            if (pointerEvent.Phase == CupriFace.Interaction.PointerPhase.Down)
            {
                _ = _workflow.StartAsync(ActivationKind.Button);
            }
            else if (pointerEvent.Phase is CupriFace.Interaction.PointerPhase.Up or CupriFace.Interaction.PointerPhase.Cancel)
            {
                _ = _workflow.StopAsync(ActivationKind.Button);
            }

            return true;
        });
        document.OnClick(".countdown-cancel", _ => _workflow.CancelPendingInjection());
        document.OnClick(".transcript-copy", _ => CopyTranscript());
        document.OnClick(".settings-open", _ => _model.Page = "settings");
        document.OnClick(".onboarding-auto", _ => ChooseRuntime(TranscriptionRuntime.Automatic));
        document.OnClick(".onboarding-cpu", _ => ChooseRuntime(TranscriptionRuntime.Cpu));
        document.OnClick(".runtime-gpu", _ => SelectRuntime(TranscriptionRuntime.Automatic));
        document.OnClick(".runtime-cpu", _ => SelectRuntime(TranscriptionRuntime.Cpu));
        document.OnClick(".model-download", pointerEvent => { _ = DownloadOrContinueAsync(); });
        document.OnClick(".config-tab-settings", _ => OpenConfigTab("settings"));
        document.OnClick(".config-tab-keybinds", _ => OpenConfigTab("keybinds"));
        document.OnClick(".config-tab-diagnostics", _ => OpenDiagnostics());
        document.OnClick(".diagnostics-refresh", _ => RefreshDiagnostics());
        document.OnClick(".model-path-open", _ => OpenModelFolder());
        document.OnClick(".config-back", _ =>
        {
            CancelBindingCapture?.Invoke();
            _model.CancelCaptureDisplay = "none";
            _model.CaptureDisplay = "none";
            _model.Page = "main";
            SaveSettings();
        });
        document.OnClick(".add-binding", _ => StartCapture());
        document.OnClick(".cancel-capture", _ => CancelBindingCapture?.Invoke());
        document.OnClick(".undo-binding", _ => UndoBindingChange());
        document.OnClick(".restore-bindings", _ => RestoreDefaultBinding());
        document.OnAction("data-remove-binding", action =>
        {
            RemoveBinding(action.Value);
            return true;
        });
    }

    private void SelectStorage(StorageMode mode)
    {
        try
        {
            _storage.Select(mode);
            _model.StorageStatus = $"Bantz data will be kept in {_storage.DisplayPath}";
            UpdateModelSetup();
            _model.Page = "onboarding";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _model.StorageStatus = "That location is not writable. Try the per-user folder or move Bantz to a writable folder.";
        }
    }

    private void ChooseRuntime(TranscriptionRuntime runtime)
    {
        SelectRuntime(runtime);
        SaveSettings();
    }

    private void SelectRuntime(TranscriptionRuntime runtime)
    {
        _model.RuntimeSelection = runtime.ToString();
        UpdateModelSetup();
    }

    private async Task DownloadOrContinueAsync()
    {
        if (_modelDownloadInProgress)
        {
            return;
        }

        SaveSettings();
        if (_engine.IsModelAvailable && _runtimeManager.IsInstalled(_model.SelectedRuntime))
        {
            _model.Page = "main";
            return;
        }

        _modelDownloadInProgress = true;
        _model.ModelDownloadLabel = "Downloading...";
        _model.ModelDownloadStatus = "Downloading the selected Whisper runtime...";
        try
        {
            var runtimeProgress = new Progress<RuntimeDownloadProgress>(value =>
            {
                _model.ModelDownloadPercent = value.Percent / 4;
                _model.ModelDownloadStatus = $"Runtime: {value.DownloadedBytes / 1_048_576d:N1} of {value.TotalBytes / 1_048_576d:N1} MiB";
            });
            await _runtimeManager.DownloadAsync(_model.SelectedRuntime, runtimeProgress);
            WhisperTranscriptionEngine.ConfigureRuntime(_model.SelectedRuntime, _runtimeManager);

            var progress = new Progress<ModelDownloadProgress>(value =>
            {
                _model.ModelDownloadPercent = 25 + (value.Percent * 3 / 4);
                _model.ModelDownloadStatus = $"Downloaded {value.DownloadedBytes / 1_048_576d:N1} of {value.TotalBytes / 1_048_576d:N1} MiB";
            });
            await _engine.DownloadModelAsync(progress);
            _model.ModelDownloadPercent = 100;
            _model.ModelDownloadLabel = "Continue";
            _model.ModelDownloadStatus = "Model downloaded. Bantz is ready to work offline.";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _model.ModelDownloadLabel = "Retry download";
            _model.ModelDownloadStatus = "Download failed. Check the connection and try again.";
        }
        finally
        {
            _modelDownloadInProgress = false;
        }
    }

    private void UpdateModelSetup()
    {
        var runtimeInstalled = _runtimeManager.IsInstalled(_model.SelectedRuntime);
        if (_engine.IsModelAvailable && runtimeInstalled)
        {
            _model.ModelDownloadPercent = 100;
            _model.ModelDownloadLabel = "Continue";
            _model.ModelDownloadStatus = "The English Base model is installed and ready.";
            return;
        }

        _model.ModelDownloadPercent = 0;
        var requiredBytes = (runtimeInstalled ? 0 : WhisperRuntimeManager.DownloadBytes(_model.SelectedRuntime)) +
            (_engine.IsModelAvailable ? 0 : WhisperTranscriptionEngine.BaseEnglishModelBytes);
        var downloadMiB = requiredBytes / 1_048_576d;
        _model.ModelDownloadLabel = $"Download {downloadMiB:N0} MiB";
        _model.ModelDownloadStatus = runtimeInstalled
            ? "The runtime is installed; download the speech model to continue."
            : "Downloads the selected runtime and speech model, then works offline.";
    }

    public void OpenDiagnostics()
    {
        RefreshDiagnostics();
        OpenConfigTab("diagnostics");
    }

    private void OpenConfigTab(string page)
    {
        if (_model.Page == "keybinds" && page != "keybinds")
        {
            CancelBindingCapture?.Invoke();
            _model.CancelCaptureDisplay = "none";
        }

        _model.Page = page;
    }

    public void RefreshDiagnostics()
    {
        var diagnostics = _engine.GetDiagnostics();
        _model.DiagnosticsStatus = diagnostics.Status;
        _model.DiagnosticsSummary = diagnostics.Summary;
        _model.DiagnosticsEngine = diagnostics.Engine;
        _model.DiagnosticsVersion = diagnostics.Version;
        _model.DiagnosticsRuntime = diagnostics.Runtime;
        _model.DiagnosticsLanguage = diagnostics.Language;
        _model.DiagnosticsModel = diagnostics.Model;
        _model.DiagnosticsModelSize = diagnostics.ModelSize;
        _model.DiagnosticsModelPath = diagnostics.ModelPath;
        _model.DiagnosticsLastRun = diagnostics.LastRun;
        _model.DiagnosticsStateClass = diagnostics.StateClass;
    }

    private void OpenModelFolder()
    {
        var modelPath = _engine.GetDiagnostics().ModelPath;
        var folder = Path.GetDirectoryName(modelPath);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            _model.DiagnosticsStatus = "Model folder unavailable";
            _model.DiagnosticsSummary = "Download the model first, then its folder can be opened here.";
            _model.DiagnosticsStateClass = "attention";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _model.DiagnosticsStatus = "Could not open model folder";
            _model.DiagnosticsSummary = "The system could not open that location. The full path is shown below.";
            _model.DiagnosticsStateClass = "attention";
        }
    }

    public void BindingCaptured(InputBinding binding)
    {
        _model.CancelCaptureDisplay = "none";
        var current = _model.GetBindingsSnapshot();
        if (current.Any(existing => existing.SameInput(binding)))
        {
            _model.CaptureState = "That input is already assigned.";
            _model.CaptureDisplay = "block";
            return;
        }

        PushBindingUndo(current);
        _model.SetBindings([.. current.Select(item => item.Copy()), binding]);
        _model.CaptureState = $"Added {binding.DisplayName}";
        _model.CaptureDisplay = "block";
        SaveSettings();
    }

    public void BindingCaptureCancelled()
    {
        _model.CancelCaptureDisplay = "none";
        _model.CaptureState = "Capture cancelled. Nothing changed.";
        _model.CaptureDisplay = "block";
    }

    private void StartCapture()
    {
        if (BeginBindingCapture?.Invoke() == true)
        {
            _model.CancelCaptureDisplay = "block";
            _model.CaptureState = "Press a key, gamepad button, or mouse button outside Bantz. Escape cancels.";
            _model.CaptureDisplay = "block";
        }
        else
        {
            _model.CancelCaptureDisplay = "none";
            _model.CaptureState = "Finish the current recording before changing an input.";
            _model.CaptureDisplay = "block";
        }
    }

    private void RemoveBinding(string id)
    {
        var current = _model.GetBindingsSnapshot();
        var updated = current.Where(binding => !string.Equals(binding.Id, id, StringComparison.Ordinal)).ToList();
        if (updated.Count == current.Count)
        {
            return;
        }

        PushBindingUndo(current);
        _model.SetBindings(updated);
        _model.CaptureState = "Binding removed. Use Undo to restore it.";
        _model.CaptureDisplay = "block";
        SaveSettings();
    }

    private void RestoreDefaultBinding()
    {
        var current = _model.GetBindingsSnapshot();
        PushBindingUndo(current);
        _model.SetBindings([InputBinding.DefaultKeyboard()]);
        _model.CaptureState = "Default shortcut restored.";
        _model.CaptureDisplay = "block";
        SaveSettings();
    }

    private void UndoBindingChange()
    {
        if (_bindingUndo is null)
        {
            _model.CaptureState = "There is no mapping change to undo.";
            _model.CaptureDisplay = "block";
            return;
        }

        _model.SetBindings(_bindingUndo);
        _bindingUndo = null;
        _model.UndoDisplay = "none";
        _model.CaptureState = "Last mapping change undone.";
        _model.CaptureDisplay = "block";
        SaveSettings();
    }

    private void PushBindingUndo(IReadOnlyList<InputBinding> current)
    {
        _bindingUndo = current.Select(binding => binding.Copy()).ToList();
        _model.UndoDisplay = "block";
    }

    private void SaveSettings()
    {
        try
        {
            WhisperTranscriptionEngine.ConfigureRuntime(_model.SelectedRuntime, _runtimeManager);
            _settingsStore.Save(_model.ToSettings());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _model.CaptureState = "Settings could not be saved; changes remain active for this session.";
            _model.CaptureDisplay = "block";
        }
    }

    private void CopyTranscript()
    {
        if (_latestTranscript.Length == 0)
        {
            _model.Status = "Nothing to copy yet";
            return;
        }

        try
        {
            _model.Status = TryWriteClipboard(_latestTranscript)
                ? "Transcript copied to clipboard"
                : "Clipboard is unavailable";
        }
        catch
        {
            _model.Status = "Could not copy the transcript";
        }
    }

    private void ApplySnapshot(DictationSnapshot snapshot)
    {
        _model.Status = snapshot.Status;
        if (snapshot.Transcript.Length > 0)
        {
            _latestTranscript = snapshot.Transcript;
        }

        _model.Transcript = _latestTranscript.Length == 0
            ? "Your latest transcript will appear here."
            : _latestTranscript;
        _model.Countdown = snapshot.CountdownSeconds > 0
            ? snapshot.CountdownSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "";
        _model.CountdownPercent = snapshot.CountdownTotalSeconds > 0
            ? snapshot.CountdownSeconds * 100 / snapshot.CountdownTotalSeconds
            : 0;
        _model.RecordLabel = snapshot.State == DictationState.Recording ? "LISTENING" : "HOLD TO TALK";
        _model.RecordHint = snapshot.State == DictationState.Recording ? "Release when you’re done" : "Release to transcribe";
        _model.StateClass = snapshot.State switch
        {
            DictationState.Recording => "recording",
            DictationState.Transcribing => "working",
            DictationState.AwaitingTarget => "targeting",
            DictationState.Error => "error",
            _ => "ready",
        };
    }
}

[CupriBindable]
public sealed partial class BantzModel
{
    private bool _autoWrite;
    private bool _autoEnter;
    private bool _alwaysOnTop;
    private bool _buttonDelayEnabled;
    private int _buttonDelaySeconds;
    private bool _shortcutDelayEnabled;
    private int _shortcutDelaySeconds;
    private List<InputBinding> _inputBindings;
    private string _runtimeSelection;

    public BantzModel(AppSettings settings)
    {
        _runtimeSelection = (settings.Runtime ?? TranscriptionRuntime.Automatic).ToString();
        _autoWrite = settings.AutoWrite;
        _autoEnter = settings.AutoEnter;
        _alwaysOnTop = settings.AlwaysOnTop;
        _buttonDelayEnabled = settings.ButtonDelayEnabled;
        _buttonDelaySeconds = Math.Clamp(settings.ButtonDelaySeconds, 0, 10);
        _shortcutDelayEnabled = settings.ShortcutDelayEnabled;
        _shortcutDelaySeconds = Math.Clamp(settings.ShortcutDelaySeconds, 0, 10);
        _inputBindings = settings.Bindings.Select(binding => binding.Copy()).ToList();
        RefreshBindingRows();
    }

    public event Action? SettingsChanged;

    public string Page { get; set; } = "main";
    public string MainDisplay => Page == "main" ? "flex" : "none";
    public string StorageDisplay => Page == "storage" ? "flex" : "none";
    public string OnboardingDisplay => Page == "onboarding" ? "flex" : "none";
    public string ConfigDisplay => Page is "settings" or "keybinds" or "diagnostics" ? "flex" : "none";
    public string SettingsTabDisplay => Page == "settings" ? "flex" : "none";
    public string KeybindsTabDisplay => Page == "keybinds" ? "flex" : "none";
    public string DiagnosticsTabDisplay => Page == "diagnostics" ? "flex" : "none";
    public string SettingsTabClass => Page == "settings" ? "selected" : "";
    public string KeybindsTabClass => Page == "keybinds" ? "selected" : "";
    public string DiagnosticsTabClass => Page == "diagnostics" ? "selected" : "";
    public string SettingsTabSelected => Page == "settings" ? "true" : "false";
    public string KeybindsTabSelected => Page == "keybinds" ? "true" : "false";
    public string DiagnosticsTabSelected => Page == "diagnostics" ? "true" : "false";
    public string Status { get; set; } = "Hold to talk";
    public string Transcript { get; set; } = "Your latest transcript will appear here.";
    public string Countdown { get; set; } = "";
    public string CountdownDisplay => Countdown.Length > 0 ? "flex" : "none";
    public string FooterDisplay => Countdown.Length > 0 ? "none" : "flex";
    public int CountdownPercent { get; set; }
    public string RecordLabel { get; set; } = "HOLD TO TALK";
    public string RecordHint { get; set; } = "Release to transcribe";
    public string StateClass { get; set; } = "ready";
    public string CaptureState { get; set; } = "Add as many inputs as you like.";
    public string CaptureDisplay { get; set; } = "none";
    public string CancelCaptureDisplay { get; set; } = "none";
    public string UndoDisplay { get; set; } = "none";
    public int BindingsListHeight => CaptureDisplay == "block" ? 205 : 250;
    public List<BindingRow> BindingRows { get; set; } = [];
    public string EmptyBindingsDisplay => BindingRows.Count == 0 ? "flex" : "none";
    public string BindingListDisplay => BindingRows.Count == 0 ? "none" : "block";
    public string DiagnosticsStatus { get; set; } = "Checking engine";
    public string DiagnosticsSummary { get; set; } = "Reading the local Whisper configuration.";
    public string DiagnosticsEngine { get; set; } = "whisper.cpp via Whisper.net";
    public string DiagnosticsVersion { get; set; } = "Unknown";
    public string DiagnosticsRuntime { get; set; } = "Not loaded yet";
    public string DiagnosticsLanguage { get; set; } = "English (en)";
    public string DiagnosticsModel { get; set; } = "Unknown";
    public string DiagnosticsModelSize { get; set; } = "Unknown";
    public string DiagnosticsModelPath { get; set; } = "Unknown";
    public string DiagnosticsLastRun { get; set; } = "Not run yet";
    public string DiagnosticsStateClass { get; set; } = "attention";
    public int ModelDownloadPercent { get; set; }
    public string ModelDownloadLabel { get; set; } = "Download 142 MiB";
    public string ModelDownloadStatus { get; set; } = "Downloads once, then transcription works offline.";
    public string StorageStatus { get; set; } = "You can move or back up the entire folder as one unit.";

    public string RuntimeSelection
    {
        get => _runtimeSelection;
        set => Set(ref _runtimeSelection, Enum.TryParse<TranscriptionRuntime>(value, true, out var runtime)
            ? runtime.ToString()
            : TranscriptionRuntime.Automatic.ToString());
    }

    public TranscriptionRuntime SelectedRuntime => Enum.TryParse<TranscriptionRuntime>(RuntimeSelection, true, out var runtime)
        ? runtime
        : TranscriptionRuntime.Automatic;

    public string GpuRuntimeClass => SelectedRuntime == TranscriptionRuntime.Automatic ? "selected" : "";
    public string CpuRuntimeClass => SelectedRuntime == TranscriptionRuntime.Cpu ? "selected" : "";
    public string GpuRuntimePressed => SelectedRuntime == TranscriptionRuntime.Automatic ? "true" : "false";
    public string CpuRuntimePressed => SelectedRuntime == TranscriptionRuntime.Cpu ? "true" : "false";

    public bool AutoWrite
    {
        get => _autoWrite;
        set => Set(ref _autoWrite, value);
    }

    public bool AutoEnter
    {
        get => _autoEnter;
        set => Set(ref _autoEnter, value);
    }

    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set => Set(ref _alwaysOnTop, value);
    }

    public bool ButtonDelayEnabled
    {
        get => _buttonDelayEnabled;
        set => Set(ref _buttonDelayEnabled, value);
    }

    public int ButtonDelaySeconds
    {
        get => _buttonDelaySeconds;
        set => Set(ref _buttonDelaySeconds, Math.Clamp(value, 0, 10));
    }

    public bool ShortcutDelayEnabled
    {
        get => _shortcutDelayEnabled;
        set => Set(ref _shortcutDelayEnabled, value);
    }

    public int ShortcutDelaySeconds
    {
        get => _shortcutDelaySeconds;
        set => Set(ref _shortcutDelaySeconds, Math.Clamp(value, 0, 10));
    }

    public int DelayFor(ActivationKind activation) => activation switch
    {
        ActivationKind.Button when ButtonDelayEnabled => ButtonDelaySeconds,
        ActivationKind.Hotkey when ShortcutDelayEnabled => ShortcutDelaySeconds,
        _ => 0,
    };

    public IReadOnlyList<InputBinding> GetBindingsSnapshot() =>
        _inputBindings.Select(binding => binding.Copy()).ToArray();

    public void SetBindings(IEnumerable<InputBinding> bindings)
    {
        _inputBindings = bindings.Select(binding => binding.Copy()).ToList();
        RefreshBindingRows();
    }

    public AppSettings ToSettings() => new()
    {
        Runtime = SelectedRuntime,
        AutoWrite = AutoWrite,
        AutoEnter = AutoEnter,
        AlwaysOnTop = AlwaysOnTop,
        ButtonDelayEnabled = ButtonDelayEnabled,
        ButtonDelaySeconds = ButtonDelaySeconds,
        ShortcutDelayEnabled = ShortcutDelayEnabled,
        ShortcutDelaySeconds = ShortcutDelaySeconds,
        Bindings = _inputBindings.Select(binding => binding.Copy()).ToList(),
    };

    private void RefreshBindingRows() => BindingRows = _inputBindings
        .Select(binding => new BindingRow
        {
            Id = binding.Id,
            Device = binding.Device switch
            {
                InputDevice.Keyboard => "KEYBOARD",
                InputDevice.Gamepad => "GAMEPAD",
                InputDevice.Mouse => "MOUSE",
                _ => "INPUT",
            },
            Name = binding.DisplayName,
        })
        .ToList();

    private void Set<T>(ref T field, T value) where T : IEquatable<T>
    {
        if (field.Equals(value))
        {
            return;
        }

        field = value;
        SettingsChanged?.Invoke();
    }
}

[CupriBindable]
public sealed partial class BindingRow
{
    public string Id { get; set; } = "";
    public string Device { get; set; } = "";
    public string Name { get; set; } = "";
}
