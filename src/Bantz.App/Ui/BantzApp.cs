using Bantz.Core;
using Bantz.Capture;
using Bantz.Input;
using Bantz.Settings;
using Bantz.Speech.Whisper;
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
    private readonly byte[] _enabledIcon;
    private readonly byte[] _disabledIcon;
    private readonly IReadOnlyList<byte[]> _recordingIconFrames;
    private readonly InitialWindowSize _initialWindowSize;
    private List<InputBinding>? _bindingUndo;
    private BindingCapturePurpose _bindingCapturePurpose;
    private bool _modelDownloadInProgress;
    private bool _iconShortcutsEnabled;
    private bool _isRecording;
    private string _latestTranscript = "";

    public BantzApp(
        DictationWorkflow workflow,
        BantzModel model,
        SettingsStore settingsStore,
        WhisperTranscriptionEngine engine,
        WhisperRuntimeManager runtimeManager,
        AppStorage storage,
        AudioSignalAnalyzer signalAnalyzer,
        InitialWindowSize? initialWindowSize = null)
    {
        _workflow = workflow;
        _model = model;
        _settingsStore = settingsStore;
        _engine = engine;
        _runtimeManager = runtimeManager;
        _storage = storage;
        _initialWindowSize = initialWindowSize is { Width: > 0, Height: > 0 } size
            ? size
            : PreferredWindowSize;
        _enabledIcon = EmbeddedAsset("Assets/BantzIcon.png").ReadBytes();
        _disabledIcon = ShortcutStateIcon.CreateDisabled(_enabledIcon);
        _recordingIconFrames = RecordingStateIcon.CreateFrames(_enabledIcon);
        _iconShortcutsEnabled = model.ShortcutsEnabled;
        _workflow.SnapshotChanged += ApplySnapshot;
        signalAnalyzer.FrameAnalyzed += ApplyAudioSignal;
        _model.SettingsChanged += SaveSettings;
        _model.SettingsChanged += UpdateModelSetup;
        _model.AdvancedBindingsVisibilityChanged += HandleAdvancedBindingsVisibilityChanged;
        UpdateModelSetup();
        ApplySnapshot(_workflow.Snapshot);
    }

    public Func<bool>? BeginBindingCapture { get; set; }
    public Action? CancelBindingCapture { get; set; }
    public event Action? ShortcutIconChanged;
    public event Action<bool>? RecordingStateChanged;

    public override string Title => "Bantz";
    public static InitialWindowSize PreferredWindowSize { get; } = new(700, 780);
    public override int Width => _initialWindowSize.Width;
    public override int Height => _initialWindowSize.Height;
    public override SKColor Background => new(0x0d, 0x10, 0x17);
    public override bool DarkWindowChrome => true;
    public override bool TopMost => _model.AlwaysOnTop;
    public override bool CloseToTray => true;
    public override byte[] Icon => _model.ShortcutsEnabled ? _enabledIcon : _disabledIcon;
    public IReadOnlyList<byte[]> RecordingIconFrames => _recordingIconFrames;
    public override object Model => _model;
    public override double RefreshIntervalSeconds => 0.1;
    protected override CupriSource MarkupSource => EmbeddedAsset("Assets/Bantz.html");
    protected override CupriSource StyleSource => EmbeddedAsset("Assets/Bantz.css");

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
        document.OnClick(".config-tab-input", _ => OpenInputDevices());
        document.OnClick(".config-tab-models", _ => OpenModels());
        document.OnClick(".input-devices-refresh", _ => RefreshInputDevices());
        document.OnClick(".config-tab-keybinds", _ => OpenConfigTab("keybinds"));
        document.OnClick(".config-tab-diagnostics", _ => OpenDiagnostics());
        document.OnClick(".config-tab-about", _ => OpenConfigTab("about"));
        document.OnClick(".diagnostics-refresh", _ => RefreshDiagnostics());
        document.OnClick(".model-path-open", _ => OpenModelFolder());
        document.OnClick(".tray-icon-settings", _ => OpenTrayIconSettings());
        document.OnClick(".config-back", _ =>
        {
            CancelBindingCapture?.Invoke();
            _bindingCapturePurpose = BindingCapturePurpose.None;
            _model.CancelCaptureDisplay = "none";
            _model.CaptureDisplay = "none";
            _model.Page = "main";
            SaveSettings();
        });
        document.OnClick(".add-binding", _ => StartCapture(BindingCapturePurpose.HoldToTalk));
        document.OnClick(".cancel-capture", _ => CancelBindingCapture?.Invoke());
        document.OnClick(".undo-binding", _ => UndoBindingChange());
        document.OnClick(".restore-bindings", _ => RestoreDefaultBinding());
        document.OnClick(".advanced-bindings", _ => ToggleAdvancedBindings());
        document.OnClick(".shortcut-info", _ => _model.ShortcutInfoExpanded = !_model.ShortcutInfoExpanded);
        document.OnClick(".set-shortcut-toggle", _ => StartCapture(BindingCapturePurpose.ShortcutToggle));
        document.OnClick(".remove-shortcut-toggle", _ => RemoveShortcutToggleBinding());
        document.OnAction("data-remove-binding", action =>
        {
            RemoveBinding(action.Value);
            return true;
        });
        document.OnAction("data-select-input-device", action =>
        {
            SelectInputDevice(action.Value);
            return true;
        });
        document.OnAction("data-select-model", action =>
        {
            SelectModel(action.Value);
            return true;
        });
        document.OnAction("data-select-language", action =>
        {
            _model.SelectLanguage(action.Value);
            _model.Status = $"Speech language set to {_model.LanguageName}";
            UpdateModelSetup();
            return true;
        });
        document.OnAction("data-remove-model", action =>
        {
            RemoveModel(action.Value);
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
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or UnauthorizedAccessException or OperationCanceledException or InvalidDataException)
        {
            _model.ModelDownloadLabel = "Retry download";
            _model.ModelDownloadStatus = exception is InvalidDataException
                ? "The download did not arrive intact. Try again."
                : "Download failed. Check the connection and try again.";
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
            _model.ModelDownloadStatus = $"{_model.SelectedModel.DisplayName} is installed and ready." + LanguageCaveat();
            return;
        }

        _model.ModelDownloadPercent = 0;
        var requiredBytes = (runtimeInstalled ? 0 : WhisperRuntimeManager.DownloadBytes(_model.SelectedRuntime)) +
            (_engine.IsModelAvailable ? 0 : _model.SelectedModel.DownloadBytes);
        var downloadMiB = requiredBytes / 1_048_576d;
        _model.ModelDownloadLabel = $"Download {downloadMiB:N0} MiB";
        _model.ModelDownloadStatus = (runtimeInstalled
            ? "The runtime is installed; the model is still needed."
            : "Downloads the runtime and model.") + LanguageCaveat();
    }

    /// <summary>
    /// Says so when the chosen language cannot apply to the chosen model, which is otherwise only
    /// discoverable by transcribing and finding English.
    /// </summary>
    private string LanguageCaveat() =>
        _model.SelectedModel.IsMultilingual || string.Equals(_model.SpeechLanguage, "en", StringComparison.Ordinal)
            ? string.Empty
            : $" {_model.LanguageName} needs a multilingual model.";

    public void OpenDiagnostics()
    {
        RefreshDiagnostics();
        OpenConfigTab("diagnostics");
    }

    private void OpenConfigTab(string page)
    {
        if (_model.Page == "keybinds" && page != "keybinds")
        {
            _model.AdvancedBindingsExpanded = false;
            CancelBindingCapture?.Invoke();
            _bindingCapturePurpose = BindingCapturePurpose.None;
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

    private static void OpenTrayIconSettings()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:taskbar",
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Windows owns this settings surface. If the URI handler is unavailable, leave Bantz running.
        }
    }

    public void BindingCaptured(InputBinding binding)
    {
        var capturePurpose = _bindingCapturePurpose;
        _bindingCapturePurpose = BindingCapturePurpose.None;
        _model.CancelCaptureDisplay = "none";
        var current = _model.GetBindingsSnapshot();
        if (capturePurpose == BindingCapturePurpose.ShortcutToggle)
        {
            if (current.Any(existing => existing.SameInput(binding)))
            {
                _model.CaptureState = "That input is already assigned to hold-to-talk.";
                _model.CaptureDisplay = "block";
                return;
            }

            _model.SetShortcutToggleBinding(binding);
            _model.CaptureState = $"{binding.DisplayName} now enables or disables PTT shortcuts.";
            _model.CaptureDisplay = "block";
            SaveSettings();
            return;
        }

        if (_model.GetShortcutToggleBindingSnapshot()?.SameInput(binding) == true)
        {
            _model.CaptureState = "That input is reserved for enabling or disabling shortcuts.";
            _model.CaptureDisplay = "block";
            return;
        }

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
        _bindingCapturePurpose = BindingCapturePurpose.None;
        _model.CancelCaptureDisplay = "none";
        _model.CaptureState = "Capture cancelled. Nothing changed.";
        _model.CaptureDisplay = "block";
    }

    private void StartCapture(BindingCapturePurpose purpose)
    {
        if (BeginBindingCapture?.Invoke() == true)
        {
            _bindingCapturePurpose = purpose;
            _model.CancelCaptureDisplay = "block";
            _model.CaptureState = purpose == BindingCapturePurpose.ShortcutToggle
                ? "Press the input that should enable or disable PTT shortcuts. Escape cancels."
                : "Press a key, gamepad button, or mouse button outside Bantz. Escape cancels.";
            _model.CaptureDisplay = "block";
        }
        else
        {
            _bindingCapturePurpose = BindingCapturePurpose.None;
            _model.CancelCaptureDisplay = "none";
            _model.CaptureState = "Finish the current recording before changing an input.";
            _model.CaptureDisplay = "block";
        }
    }

    private void OpenModels()
    {
        OpenConfigTab("models");
        RefreshInstalledModels();
    }

    /// <summary>Re-reads which models are on disk.</summary>
    public void RefreshInstalledModels()
    {
        var installed = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var model in WhisperModelCatalog.All)
        {
            try
            {
                var file = new FileInfo(_engine.PathFor(model));
                if (file.Exists)
                {
                    installed[model.Id] = file.Length;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A model we cannot stat is a model we cannot offer to delete.
            }
        }

        _model.SetInstalledModels(installed);
    }

    private void SelectModel(string id)
    {
        var model = WhisperModelCatalog.Resolve(id);
        _model.SelectModel(model.Id);
        UpdateModelSetup();
        RefreshInstalledModels();
        if (_engine.IsInstalled(model))
        {
            _model.Status = $"Speech model set to {model.DisplayName}";
            return;
        }

        _model.Status = $"Downloading {model.DisplayName}…";
        _ = DownloadSelectedModelAsync(model);
    }

    private async Task DownloadSelectedModelAsync(WhisperModel model)
    {
        if (_modelDownloadInProgress)
        {
            return;
        }

        _modelDownloadInProgress = true;
        _model.SetDownloadingModel(model.Id);
        try
        {
            var progress = new Progress<ModelDownloadProgress>(value =>
                _model.ModelDownloadPercent = value.Percent);
            await _engine.DownloadModelAsync(model, progress);
            _model.Status = $"{model.DisplayName} is ready";
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _model.Status = $"{model.DisplayName} could not be downloaded: {exception.Message}";
        }
        finally
        {
            _modelDownloadInProgress = false;
            _model.SetDownloadingModel(null);
            RefreshInstalledModels();
            UpdateModelSetup();
            RefreshDiagnostics();
        }
    }

    private void RemoveModel(string id)
    {
        var model = WhisperModelCatalog.Resolve(id);
        if (string.Equals(model.Id, _model.SelectedModel.Id, StringComparison.Ordinal))
        {
            _model.Status = "That model is in use. Choose another one first.";
            return;
        }

        try
        {
            _model.Status = _engine.DeleteModel(model)
                ? $"Deleted {model.DisplayName}"
                : $"{model.DisplayName} was not downloaded";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _model.Status = $"{model.DisplayName} could not be deleted: {exception.Message}";
        }

        RefreshInstalledModels();
    }

    private void OpenInputDevices()
    {
        OpenConfigTab("input");
        RefreshInputDevices();
    }

    /// <summary>Re-reads the microphone list so devices connected since startup appear.</summary>
    public void RefreshInputDevices() => _model.SetInputDevices(AudioCaptureDevices.List());

    private void SelectInputDevice(string id)
    {
        _model.SelectInputDevice(id);
        _model.Status = $"Microphone set to {_model.SelectedInputDeviceName}";
    }

    private void ToggleAdvancedBindings()
    {
        if (!_model.AdvancedBindingsExpanded)
        {
            CancelBindingCapture?.Invoke();
            _bindingCapturePurpose = BindingCapturePurpose.None;
            _model.CancelCaptureDisplay = "none";
            _model.CaptureDisplay = "none";
        }

        _model.AdvancedBindingsExpanded = !_model.AdvancedBindingsExpanded;
    }

    private void HandleAdvancedBindingsVisibilityChanged(bool isVisible)
    {
        if (isVisible)
        {
            return;
        }

        CancelBindingCapture?.Invoke();
        _bindingCapturePurpose = BindingCapturePurpose.None;
        _model.CancelCaptureDisplay = "none";
        _model.CaptureDisplay = "none";
    }

    private void RemoveShortcutToggleBinding()
    {
        if (_model.GetShortcutToggleBindingSnapshot() is null)
        {
            return;
        }

        _model.SetShortcutToggleBinding(null);
        _model.CaptureState = "The enable/disable shortcut was removed.";
        _model.CaptureDisplay = "block";
        SaveSettings();
    }

    public void ToggleShortcutsEnabled()
    {
        _model.ShortcutsEnabled = !_model.ShortcutsEnabled;
        var state = _model.ShortcutsEnabled ? "enabled" : "disabled";
        _model.Status = $"PTT shortcuts {state}";
        _model.CaptureState = $"PTT shortcuts are {state}.";
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
        var iconChanged = _iconShortcutsEnabled != _model.ShortcutsEnabled;
        _iconShortcutsEnabled = _model.ShortcutsEnabled;
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

        if (iconChanged)
        {
            ShortcutIconChanged?.Invoke();
        }
    }

    private enum BindingCapturePurpose
    {
        None,
        HoldToTalk,
        ShortcutToggle,
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
        var isRecording = snapshot.State == DictationState.Recording;
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
        _model.RecordLabel = isRecording ? "LISTENING" : "HOLD TO TALK";
        _model.RecordHint = isRecording ? "Release when you’re done" : "Release to transcribe";
        if (!isRecording)
        {
            _model.RecordingBarOneScale = "0.43";
            _model.RecordingBarTwoScale = "1.00";
            _model.RecordingBarThreeScale = "0.67";
        }
        _model.StateClass = snapshot.State switch
        {
            DictationState.Recording => "recording",
            DictationState.Transcribing => "working",
            DictationState.AwaitingTarget => "targeting",
            DictationState.Error => "error",
            _ => "ready",
        };

        if (_isRecording != isRecording)
        {
            _isRecording = isRecording;
            RecordingStateChanged?.Invoke(isRecording);
        }
    }

    private void ApplyAudioSignal(AudioSignalFrame frame)
    {
        _model.RecordingBarOneScale = Scale(frame.FirstBar);
        _model.RecordingBarTwoScale = Scale(frame.SecondBar);
        _model.RecordingBarThreeScale = Scale(frame.ThirdBar);
    }

    private static string Scale(float value) =>
        value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}

[CupriBindable]
public sealed partial class BantzModel
{
    private bool _autoWrite;
    private bool _autoEnter;
    private bool _clipboardPaste;
    private bool _alwaysOnTop;
    private bool _buttonDelayEnabled;
    private int _buttonDelaySeconds;
    private bool _shortcutDelayEnabled;
    private int _shortcutDelaySeconds;
    private bool _shortcutsEnabled;
    private string _modelId = WhisperModelCatalog.DefaultModelId;
    private string _speechLanguage = "en";
    private Dictionary<string, long> _installedModels = new(StringComparer.Ordinal);
    private string? _downloadingModelId;
    private string? _captureDeviceId;
    private string? _captureDeviceName;
    private IReadOnlyList<AudioCaptureDevice> _inputDevices = [];
    private InputBinding? _shortcutToggleBinding;
    private bool _advancedBindingsExpanded;
    private bool _shortcutInfoExpanded;
    private readonly bool _traySettingsAvailable = OperatingSystem.IsWindows();
    private List<InputBinding> _inputBindings;
    private string _runtimeSelection;

    public BantzModel(AppSettings settings)
    {
        _runtimeSelection = (settings.Runtime ?? TranscriptionRuntime.Automatic).ToString();
        _autoWrite = settings.AutoWrite;
        _autoEnter = settings.AutoEnter;
        _clipboardPaste = settings.ClipboardPaste;
        _alwaysOnTop = settings.AlwaysOnTop;
        _buttonDelayEnabled = settings.ButtonDelayEnabled;
        _buttonDelaySeconds = Math.Clamp(settings.ButtonDelaySeconds, 0, 10);
        _shortcutDelayEnabled = settings.ShortcutDelayEnabled;
        _shortcutDelaySeconds = Math.Clamp(settings.ShortcutDelaySeconds, 0, 10);
        _shortcutsEnabled = settings.ShortcutsEnabled;
        _modelId = WhisperModelCatalog.Resolve(settings.ModelId).Id;
        _speechLanguage = SpeechLanguages.Find(settings.Language)?.Code ?? "en";
        _captureDeviceId = settings.CaptureDeviceId;
        _captureDeviceName = settings.CaptureDeviceName;
        _shortcutToggleBinding = settings.ShortcutToggleBinding?.Copy();
        _inputBindings = settings.Bindings.Select(binding => binding.Copy()).ToList();
        RefreshBindingRows();
        RefreshInputDeviceRows();
        RefreshModelRows();
    }

    public event Action? SettingsChanged;

    public string Page { get; set; } = "main";
    public string AppVersion { get; } = typeof(BantzModel).Assembly.GetName().Version?.ToString(3) ?? "Development";
    public string MainDisplay => Page == "main" ? "flex" : "none";
    public string StorageDisplay => Page == "storage" ? "flex" : "none";
    public string OnboardingDisplay => Page == "onboarding" ? "flex" : "none";
    public string ConfigDisplay =>
        Page is "settings" or "input" or "models" or "keybinds" or "diagnostics" or "about" ? "flex" : "none";
    public string SettingsTabDisplay => Page == "settings" ? "flex" : "none";
    public string InputTabDisplay => Page == "input" ? "flex" : "none";
    public string ModelsTabDisplay => Page == "models" ? "flex" : "none";
    public string KeybindsTabDisplay => Page == "keybinds" ? "flex" : "none";
    public string DiagnosticsTabDisplay => Page == "diagnostics" ? "flex" : "none";
    public string AboutTabDisplay => Page == "about" ? "flex" : "none";
    public string SettingsTabClass => Page == "settings" ? "selected" : "";
    public string InputTabClass => Page == "input" ? "selected" : "";
    public string ModelsTabClass => Page == "models" ? "selected" : "";
    public string KeybindsTabClass => Page == "keybinds" ? "selected" : "";
    public string DiagnosticsTabClass => Page == "diagnostics" ? "selected" : "";
    public string AboutTabClass => Page == "about" ? "selected" : "";
    public string SettingsTabSelected => Page == "settings" ? "true" : "false";
    public string InputTabSelected => Page == "input" ? "true" : "false";
    public string ModelsTabSelected => Page == "models" ? "true" : "false";
    public string KeybindsTabSelected => Page == "keybinds" ? "true" : "false";
    public string DiagnosticsTabSelected => Page == "diagnostics" ? "true" : "false";
    public string AboutTabSelected => Page == "about" ? "true" : "false";
    public string TraySettingsDisplay => _traySettingsAvailable ? "flex" : "none";
    public string Status { get; set; } = "Hold to talk";
    public string Transcript { get; set; } = "Your latest transcript will appear here.";
    public string Countdown { get; set; } = "";
    public string CountdownDisplay => Countdown.Length > 0 ? "flex" : "none";
    public string FooterDisplay => Countdown.Length > 0 ? "none" : "flex";
    public int CountdownPercent { get; set; }
    public string RecordLabel { get; set; } = "HOLD TO TALK";
    public string RecordHint { get; set; } = "Release to transcribe";
    public string RecordingBarOneScale { get; set; } = "0.43";
    public string RecordingBarTwoScale { get; set; } = "1.00";
    public string RecordingBarThreeScale { get; set; } = "0.67";
    public string StateClass { get; set; } = "ready";
    public string RecordShortcutClass => ShortcutsEnabled ? "" : "shortcuts-disabled";
    public string CaptureState { get; set; } = "Add as many inputs as you like.";
    public string CaptureDisplay { get; set; } = "none";
    public string CancelCaptureDisplay { get; set; } = "none";
    public string UndoDisplay { get; set; } = "none";
    public string PrimaryCaptureDisplay => AdvancedBindingsExpanded ? "none" : CaptureDisplay;
    public string AdvancedCaptureDisplay => AdvancedBindingsExpanded && CaptureDisplay == "block" ? "flex" : "none";
    public string PrimaryCancelCaptureDisplay => AdvancedBindingsExpanded ? "none" : CancelCaptureDisplay;
    public string AdvancedCancelCaptureDisplay => AdvancedBindingsExpanded ? CancelCaptureDisplay : "none";
    public int BindingsListHeight => PrimaryCaptureDisplay == "block" ? 205 : 250;
    public List<BindingRow> BindingRows { get; set; } = [];
    public List<InputDeviceRow> InputDeviceRows { get; set; } = [];
    public List<SpeechModelRow> SpeechModelRows { get; set; } = [];
    public List<SpeechLanguageRow> SpeechLanguageRows { get; set; } = [];

    /// <summary>The model transcription will use.</summary>
    public WhisperModel SelectedModel => WhisperModelCatalog.Resolve(_modelId);

    /// <summary>The chosen model's id, for pickers that bind to a value.</summary>
    public string SelectedModelId
    {
        get => SelectedModel.Id;
        set => SelectModel(value);
    }

    /// <summary>The language the person chose, whether or not this model can honour it.</summary>
    public string SpeechLanguage
    {
        get => _speechLanguage;
        set => SelectLanguage(value);
    }

    /// <summary>The language transcription will actually ask for.</summary>
    public string EffectiveLanguage => SpeechLanguages.Resolve(_speechLanguage, SelectedModel);

    public string SelectedModelName => SelectedModel.DisplayName;
    public string LanguageLockedDisplay => SelectedModel.IsMultilingual ? "none" : "flex";
    public string ModelStatus
    {
        get
        {
            if (_downloadingModelId is { } downloading)
            {
                return $"Downloading {WhisperModelCatalog.Resolve(downloading).DisplayName}…";
            }

            var description = $"{SelectedModel.DisplayName} · {SelectedModel.Summary}";
            var transcribes = SelectedModel.IsMultilingual
                ? $"Transcribes {EffectiveLanguageName}."
                : string.Equals(_speechLanguage, "en", StringComparison.Ordinal)
                    ? "Transcribes English only."
                    : $"Transcribes English only; {LanguageName} applies once a multilingual model is chosen.";
            return _installedModels.ContainsKey(SelectedModel.Id)
                ? $"{description}. {transcribes}"
                : $"{description}. Downloads on the first transcription. {transcribes}";
        }
    }

    public string LanguageName => SpeechLanguages.Find(_speechLanguage)?.Name ?? "English";
    public string EffectiveLanguageName => SpeechLanguages.Find(EffectiveLanguage)?.Name ?? "English";
    public string InputDeviceListDisplay => InputDeviceRows.Count == 0 ? "none" : "block";
    public string EmptyInputDevicesDisplay => InputDeviceRows.Count == 0 ? "flex" : "none";
    public string SelectedInputDeviceName => _captureDeviceName ?? _captureDeviceId ?? "System default";
    public string InputDeviceStatus => _captureDeviceId is null
        ? "Bantz records from whichever microphone Windows is set to use."
        : SelectedInputDeviceAvailable
            ? $"Bantz records from {SelectedInputDeviceName}."
            : $"{SelectedInputDeviceName} is not connected. Bantz uses the system default until it returns.";
    public string InputDeviceStatusClass => _captureDeviceId is not null && !SelectedInputDeviceAvailable
        ? "device-missing"
        : "";
    public string EmptyBindingsDisplay => BindingRows.Count == 0 ? "flex" : "none";
    public string BindingListDisplay => BindingRows.Count == 0 ? "none" : "block";
    public bool AdvancedBindingsExpanded
    {
        get => _advancedBindingsExpanded;
        set
        {
            if (_advancedBindingsExpanded == value)
            {
                return;
            }

            _advancedBindingsExpanded = value;
            AdvancedBindingsVisibilityChanged?.Invoke(value);
        }
    }
    internal event Action<bool>? AdvancedBindingsVisibilityChanged;
    public string ShortcutToggleHint => _shortcutToggleBinding is { } toggle
        ? $"Global hold-to-talk inputs. {toggle.DisplayName} toggles them from anywhere."
        : "Global hold-to-talk inputs. Assign a toggle input under Keybinds > Advanced.";
    public string ShortcutStateLabel => ShortcutsEnabled ? "Enabled" : "Disabled";
    public string ShortcutStateClass => ShortcutsEnabled ? "enabled" : "disabled";
    public string ShortcutToggleBindingName => _shortcutToggleBinding?.DisplayName ?? "Not assigned";
    public string ShortcutToggleButtonLabel => _shortcutToggleBinding is null ? "Set input" : "Change";
    public string ShortcutToggleRemoveDisplay => _shortcutToggleBinding is null ? "none" : "block";
    public bool ShortcutInfoExpanded
    {
        get => _shortcutInfoExpanded;
        set => _shortcutInfoExpanded = value;
    }
    public string ShortcutInfoDisplay => ShortcutInfoExpanded ? "block" : "none";
    public string ShortcutInfoLabel => ShortcutInfoExpanded ? "Hide explanation" : "Why use this?";
    public string ShortcutFooterText => ShortcutsEnabled
        ? "Keyboard, gamepad, and mouse shortcuts are available anywhere."
        : "PTT shortcuts are disabled. Use Settings to re-enable them.";
    public string ShortcutFooterClass => ShortcutsEnabled ? "" : "shortcuts-disabled";
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

    public bool ClipboardPaste
    {
        get => _clipboardPaste;
        set => Set(ref _clipboardPaste, value);
    }

    public string ClipboardPasteHint => ClipboardPaste
        ? "Ctrl+V pastes each transcript, then your clipboard returns."
        : "Uses the clipboard, for Remote Desktop and similar apps.";

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

    public bool ShortcutsEnabled
    {
        get => _shortcutsEnabled;
        set => Set(ref _shortcutsEnabled, value);
    }

    public int DelayFor(ActivationKind activation) => activation switch
    {
        ActivationKind.Button when ButtonDelayEnabled => ButtonDelaySeconds,
        ActivationKind.Hotkey when ShortcutDelayEnabled => ShortcutDelaySeconds,
        _ => 0,
    };

    /// <summary>Replaces the cached microphone list, keeping the current choice selected.</summary>
    public void SetInputDevices(IReadOnlyList<AudioCaptureDevice> devices)
    {
        _inputDevices = [.. devices];
        RefreshInputDeviceRows();
    }

    /// <summary>Chooses a microphone by id; an unknown id falls back to the system default.</summary>
    public void SelectInputDevice(string id)
    {
        var device = _inputDevices.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.Ordinal));
        var isDefault = device is null ||
            string.Equals(device.Id, AudioCaptureDevices.DefaultId, StringComparison.Ordinal);
        var selectedId = isDefault ? null : device!.Id;
        var selectedName = isDefault ? null : device!.Name;
        var changed = !string.Equals(_captureDeviceId, selectedId, StringComparison.Ordinal) ||
            !string.Equals(_captureDeviceName, selectedName, StringComparison.Ordinal);
        _captureDeviceId = selectedId;
        _captureDeviceName = selectedName;
        RefreshInputDeviceRows();
        if (changed)
        {
            SettingsChanged?.Invoke();
        }
    }

    /// <summary>
    /// The device id capture should use now. Windows wave-in ids are positional, so a saved name
    /// re-finds the same microphone after the device order changes; a device that is gone falls
    /// back to the system default rather than failing the next recording.
    /// </summary>
    public string? ResolveCaptureDeviceId()
    {
        if (_captureDeviceId is null || _inputDevices.Count == 0)
        {
            return _captureDeviceId;
        }

        if (_captureDeviceName is not null)
        {
            // The saved name decides, because a positional id can now point at a different
            // microphone; when the named device is absent, the system default is the honest
            // choice rather than recording from whatever took its place.
            return _inputDevices.FirstOrDefault(device => MatchesSavedName(device))?.Id;
        }

        return _inputDevices.Any(device => string.Equals(device.Id, _captureDeviceId, StringComparison.Ordinal))
            ? _captureDeviceId
            : null;
    }

    /// <summary>The capture options for the next recording session.</summary>
    public AudioCaptureOptions CaptureOptions() => new(ResolveCaptureDeviceId());

    /// <summary>
    /// Compares a device against the saved name. A name saved before Bantz read full names from
    /// Core Audio was cut short by the wave-in API, so a saved name that begins the device's name
    /// still counts as the same microphone.
    /// </summary>
    private bool MatchesSavedName(AudioCaptureDevice device) =>
        !string.Equals(device.Id, AudioCaptureDevices.DefaultId, StringComparison.Ordinal) &&
        _captureDeviceName is not null &&
        (string.Equals(device.Name, _captureDeviceName, StringComparison.Ordinal) ||
            device.Name.StartsWith(_captureDeviceName, StringComparison.Ordinal));

    private bool SelectedInputDeviceAvailable => ResolveCaptureDeviceId() is not null;

    private string SelectedInputDeviceId => ResolveCaptureDeviceId() ?? AudioCaptureDevices.DefaultId;

    /// <summary>Records which models are on disk, with their sizes.</summary>
    public void SetInstalledModels(IReadOnlyDictionary<string, long> installed)
    {
        _installedModels = new Dictionary<string, long>(installed, StringComparer.Ordinal);
        RefreshModelRows();
    }

    /// <summary>Shows a model as downloading, or clears the indicator when passed null.</summary>
    public void SetDownloadingModel(string? modelId)
    {
        _downloadingModelId = modelId;
        RefreshModelRows();
    }

    /// <summary>Chooses the model to transcribe with. An unknown id falls back to the default.</summary>
    public void SelectModel(string id)
    {
        var model = WhisperModelCatalog.Resolve(id);
        var changed = !string.Equals(model.Id, _modelId, StringComparison.Ordinal);
        _modelId = model.Id;
        RefreshModelRows();
        if (changed)
        {
            SettingsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Chooses the language to transcribe. The choice is remembered even while an English-only
    /// model is in use, and takes effect as soon as a multilingual model is chosen.
    /// </summary>
    public void SelectLanguage(string code)
    {
        var language = SpeechLanguages.Find(code)?.Code ?? "en";
        var changed = !string.Equals(language, _speechLanguage, StringComparison.Ordinal);
        _speechLanguage = language;
        RefreshModelRows();
        if (changed)
        {
            SettingsChanged?.Invoke();
        }
    }

    public IReadOnlyList<InputBinding> GetBindingsSnapshot() =>
        _inputBindings.Select(binding => binding.Copy()).ToArray();

    public InputBinding? GetShortcutToggleBindingSnapshot() => _shortcutToggleBinding?.Copy();

    public void SetBindings(IEnumerable<InputBinding> bindings)
    {
        _inputBindings = bindings.Select(binding => binding.Copy()).ToList();
        RefreshBindingRows();
    }

    public void SetShortcutToggleBinding(InputBinding? binding) =>
        _shortcutToggleBinding = binding?.Copy();

    public AppSettings ToSettings() => new()
    {
        Runtime = SelectedRuntime,
        ModelId = SelectedModel.Id,
        Language = _speechLanguage,
        AutoWrite = AutoWrite,
        AutoEnter = AutoEnter,
        ClipboardPaste = ClipboardPaste,
        AlwaysOnTop = AlwaysOnTop,
        ButtonDelayEnabled = ButtonDelayEnabled,
        ButtonDelaySeconds = ButtonDelaySeconds,
        ShortcutDelayEnabled = ShortcutDelayEnabled,
        ShortcutDelaySeconds = ShortcutDelaySeconds,
        ShortcutsEnabled = ShortcutsEnabled,
        CaptureDeviceId = _captureDeviceId,
        CaptureDeviceName = _captureDeviceName,
        ShortcutToggleBinding = _shortcutToggleBinding?.Copy(),
        Bindings = _inputBindings.Select(binding => binding.Copy()).ToList(),
    };

    private void RefreshModelRows()
    {
        SpeechLanguageRows = SpeechLanguages.All
            .Select(language =>
            {
                var selected = string.Equals(language.Code, _speechLanguage, StringComparison.Ordinal);
                return new SpeechLanguageRow
                {
                    Code = language.Code,
                    Name = language.Name,
                    RowClass = selected ? "selected" : "",
                    ActionLabel = selected ? "In use" : "Use",
                };
            })
            .ToList();

        SpeechModelRows = WhisperModelCatalog.All
            .Select(model =>
            {
                var installed = _installedModels.TryGetValue(model.Id, out var size);
                var selected = string.Equals(model.Id, SelectedModel.Id, StringComparison.Ordinal);
                var downloading = string.Equals(model.Id, _downloadingModelId, StringComparison.Ordinal);
                return new SpeechModelRow
                {
                    Id = model.Id,
                    Name = model.DisplayName,
                    Badge = model.IsMultilingual ? "MULTI" : "ENGLISH",
                    Summary = downloading
                        ? "Downloading…"
                        : installed
                            ? $"{size / 1_048_576d:N0} MiB"
                            : $"{model.DownloadBytes / 1_048_576d:N0} MiB",
                    RowClass = selected ? "selected" : "",
                    ActionLabel = selected ? "In use" : "Use",
                    InstalledDisplay = installed && !downloading ? "flex" : "none",
                    RemoveDisplay = installed && !selected && !downloading ? "block" : "none",
                };
            })
            .ToList();

    }

    private void RefreshInputDeviceRows()
    {
        var selectedId = SelectedInputDeviceId;
        InputDeviceRows = _inputDevices
            .Select(device =>
            {
                var selected = string.Equals(device.Id, selectedId, StringComparison.Ordinal);
                return new InputDeviceRow
                {
                    Id = device.Id,
                    Badge = string.Equals(device.Id, AudioCaptureDevices.DefaultId, StringComparison.Ordinal)
                        ? "DEFAULT"
                        : "MIC",
                    Name = device.Name,
                    RowClass = selected ? "selected" : "",
                    ActionLabel = selected ? "In use" : "Use",
                };
            })
            .ToList();
    }

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

[CupriBindable]
public sealed partial class SpeechLanguageRow
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string RowClass { get; set; } = "";
    public string ActionLabel { get; set; } = "";
}

[CupriBindable]
public sealed partial class SpeechModelRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Badge { get; set; } = "";
    public string Summary { get; set; } = "";
    public string RowClass { get; set; } = "";
    public string ActionLabel { get; set; } = "";
    public string InstalledDisplay { get; set; } = "none";
    public string RemoveDisplay { get; set; } = "none";
}


[CupriBindable]
public sealed partial class InputDeviceRow
{
    public string Id { get; set; } = "";
    public string Badge { get; set; } = "";
    public string Name { get; set; } = "";
    public string RowClass { get; set; } = "";
    public string ActionLabel { get; set; } = "";
}
