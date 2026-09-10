using Bantz.Capture;
using Bantz.Settings;
using Bantz.Speech.Whisper;
using Bantz.Input;
using Bantz.Ui;
using Xunit;

namespace Bantz.Core.Tests;

public sealed class BantzModelTests
{
    [Theory]
    [InlineData("settings", "flex", "selected", "", "", "", "", "")]
    [InlineData("input", "flex", "", "selected", "", "", "", "")]
    [InlineData("models", "flex", "", "", "selected", "", "", "")]
    [InlineData("keybinds", "flex", "", "", "", "selected", "", "")]
    [InlineData("diagnostics", "flex", "", "", "", "", "selected", "")]
    [InlineData("about", "flex", "", "", "", "", "", "selected")]
    [InlineData("main", "none", "", "", "", "", "", "")]
    public void ConfigurationTabsExposeOneSelectedPage(
        string page,
        string configDisplay,
        string settingsClass,
        string inputClass,
        string modelsClass,
        string keybindsClass,
        string diagnosticsClass,
        string aboutClass)
    {
        var model = new BantzModel(AppSettings.Defaults()) { Page = page };

        Assert.Equal(configDisplay, model.ConfigDisplay);
        Assert.Equal(settingsClass, model.SettingsTabClass);
        Assert.Equal(inputClass, model.InputTabClass);
        Assert.Equal(modelsClass, model.ModelsTabClass);
        Assert.Equal(keybindsClass, model.KeybindsTabClass);
        Assert.Equal(diagnosticsClass, model.DiagnosticsTabClass);
        Assert.Equal(aboutClass, model.AboutTabClass);
    }

    [Fact]
    public void ANewInstallTranscribesEnglishWithTheBaseModel()
    {
        var model = new BantzModel(AppSettings.Defaults());

        Assert.Equal("base.en", model.SelectedModel.Id);
        Assert.Equal("en", model.SpeechLanguage);
        Assert.Equal("flex", model.LanguageLockedDisplay);
        Assert.Equal("none", model.LanguageRowsDisplay);
    }

    [Fact]
    public void ChoosingAMultilingualModelOffersTheLanguageChoice()
    {
        var model = new BantzModel(AppSettings.Defaults());
        var saves = 0;
        model.SettingsChanged += () => saves++;

        model.SelectModel("small");
        model.SpeechLanguage = "fr";

        Assert.Equal("small", model.ToSettings().ModelId);
        Assert.Equal("fr", model.ToSettings().Language);
        Assert.Equal("French", model.LanguageName);
        Assert.Equal("block", model.LanguageRowsDisplay);
        Assert.Equal("none", model.LanguageLockedDisplay);
        Assert.Equal(2, saves);
    }

    [Fact]
    public void AnEnglishOnlyModelKeepsEnglishWhateverTheLanguageSays()
    {
        var settings = AppSettings.Defaults();
        settings.ModelId = "small";
        settings.Language = "de";
        var model = new BantzModel(settings);
        Assert.Equal("de", model.SpeechLanguage);

        model.SelectModel("base.en");

        Assert.Equal("en", model.SpeechLanguage);
        Assert.Equal("en", model.ToSettings().Language);
    }

    [Fact]
    public void AnUnknownModelFallsBackToTheDefault()
    {
        var settings = AppSettings.Defaults();
        settings.ModelId = "gargantuan-v9";
        var model = new BantzModel(settings);

        Assert.Equal(WhisperModelCatalog.DefaultModelId, model.SelectedModel.Id);
    }

    [Fact]
    public void ModelRowsShowWhatIsInstalledAndWhatWouldBeDownloaded()
    {
        var model = new BantzModel(AppSettings.Defaults());
        model.SetInstalledModels(new Dictionary<string, long> { ["base.en"] = 147_964_211 });

        var installed = model.SpeechModelRows.Single(row => row.Id == "base.en");
        var absent = model.SpeechModelRows.Single(row => row.Id == "small");

        Assert.Equal("selected", installed.RowClass);
        Assert.Equal("In use", installed.ActionLabel);
        Assert.Contains("Installed", installed.Summary, StringComparison.Ordinal);
        Assert.Equal("none", installed.RemoveDisplay);
        Assert.Contains("466 MiB", absent.Summary, StringComparison.Ordinal);
        Assert.Equal("none", absent.RemoveDisplay);
    }

    [Fact]
    public void AnInstalledModelThatIsNotInUseCanBeDeleted()
    {
        var model = new BantzModel(AppSettings.Defaults());
        model.SetInstalledModels(new Dictionary<string, long>
        {
            ["base.en"] = 147_964_211,
            ["small"] = 488_000_000,
        });

        Assert.Equal("block", model.SpeechModelRows.Single(row => row.Id == "small").RemoveDisplay);
    }

    [Fact]
    public void ADownloadingModelSaysSoAndCannotBeDeleted()
    {
        var model = new BantzModel(AppSettings.Defaults());
        model.SetInstalledModels(new Dictionary<string, long> { ["small"] = 488_000_000 });

        model.SetDownloadingModel("small");

        var row = model.SpeechModelRows.Single(candidate => candidate.Id == "small");
        Assert.Equal("Downloading…", row.Summary);
        Assert.Equal("none", row.RemoveDisplay);
        Assert.Contains("Downloading Small", model.ModelStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void CompatibilityModeIsOffUntilItIsTurnedOn()
    {
        var model = new BantzModel(AppSettings.Defaults());

        Assert.False(model.ClipboardPaste);
        Assert.False(model.ToSettings().ClipboardPaste);
        Assert.Contains("Remote Desktop", model.ClipboardPasteHint, StringComparison.Ordinal);
        Assert.Contains("clipboard", model.ClipboardPasteHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompatibilityModeSavesAndExplainsItself()
    {
        var model = new BantzModel(AppSettings.Defaults());
        var saves = 0;
        model.SettingsChanged += () => saves++;

        model.ClipboardPaste = true;

        Assert.True(model.ToSettings().ClipboardPaste);
        Assert.Equal(1, saves);
        Assert.Contains("Ctrl+V", model.ClipboardPasteHint, StringComparison.Ordinal);
        Assert.Contains("clipboard", model.ClipboardPasteHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSystemDefaultMicrophoneIsSelectedUntilOneIsChosen()
    {
        var model = new BantzModel(AppSettings.Defaults());
        model.SetInputDevices([AudioCaptureDevices.Default, new AudioCaptureDevice("1", "Blue Yeti")]);

        var rows = model.InputDeviceRows;

        Assert.Null(model.ResolveCaptureDeviceId());
        Assert.Equal("selected", rows[0].RowClass);
        Assert.Equal("In use", rows[0].ActionLabel);
        Assert.Equal("", rows[1].RowClass);
        Assert.Equal("Use", rows[1].ActionLabel);
    }

    [Fact]
    public void ChoosingAMicrophoneSavesItsIdAndName()
    {
        var model = new BantzModel(AppSettings.Defaults());
        var saves = 0;
        model.SettingsChanged += () => saves++;
        model.SetInputDevices([AudioCaptureDevices.Default, new AudioCaptureDevice("1", "Blue Yeti")]);

        model.SelectInputDevice("1");
        var settings = model.ToSettings();

        Assert.Equal("1", settings.CaptureDeviceId);
        Assert.Equal("Blue Yeti", settings.CaptureDeviceName);
        Assert.Equal("Blue Yeti", model.SelectedInputDeviceName);
        Assert.Equal(new AudioCaptureOptions("1"), model.CaptureOptions());
        Assert.Equal(1, saves);
    }

    [Fact]
    public void ChoosingTheDefaultClearsTheSavedMicrophone()
    {
        var settings = AppSettings.Defaults();
        settings.CaptureDeviceId = "1";
        settings.CaptureDeviceName = "Blue Yeti";
        var model = new BantzModel(settings);
        model.SetInputDevices([AudioCaptureDevices.Default, new AudioCaptureDevice("1", "Blue Yeti")]);

        model.SelectInputDevice(AudioCaptureDevices.DefaultId);

        Assert.Null(model.ToSettings().CaptureDeviceId);
        Assert.Null(model.ToSettings().CaptureDeviceName);
        Assert.Null(model.ResolveCaptureDeviceId());
    }

    [Fact]
    public void AMicrophoneIsFoundAgainByNameWhenDeviceNumbersShift()
    {
        var settings = AppSettings.Defaults();
        settings.CaptureDeviceId = "1";
        settings.CaptureDeviceName = "Blue Yeti";
        var model = new BantzModel(settings);

        model.SetInputDevices([
            AudioCaptureDevices.Default,
            new AudioCaptureDevice("1", "Webcam"),
            new AudioCaptureDevice("2", "Blue Yeti"),
        ]);

        Assert.Equal("2", model.ResolveCaptureDeviceId());
        Assert.Equal("selected", model.InputDeviceRows[2].RowClass);
    }

    [Fact]
    public void AMicrophoneSavedUnderItsTruncatedNameStillMatches()
    {
        // Saved by a build that read names through wave-in, which cuts them to 31 characters.
        var settings = AppSettings.Defaults();
        settings.CaptureDeviceId = "1";
        settings.CaptureDeviceName = "Microphone (SteelSeries Arctis";
        var model = new BantzModel(settings);

        model.SetInputDevices([
            AudioCaptureDevices.Default,
            new AudioCaptureDevice("1", "Microphone (SteelSeries Arctis 7 Chat)"),
        ]);

        Assert.Equal("1", model.ResolveCaptureDeviceId());
        Assert.Equal("selected", model.InputDeviceRows[1].RowClass);
    }

    [Fact]
    public void AMissingMicrophoneFallsBackToTheDefaultAndSaysSo()
    {
        var settings = AppSettings.Defaults();
        settings.CaptureDeviceId = "1";
        settings.CaptureDeviceName = "Blue Yeti";
        var model = new BantzModel(settings);

        model.SetInputDevices([AudioCaptureDevices.Default, new AudioCaptureDevice("1", "Webcam")]);

        Assert.Null(model.ResolveCaptureDeviceId());
        Assert.Equal("device-missing", model.InputDeviceStatusClass);
        Assert.Contains("Blue Yeti is not connected", model.InputDeviceStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void ASavedMicrophoneIsKeptWhileTheDeviceListIsUnknown()
    {
        var settings = AppSettings.Defaults();
        settings.CaptureDeviceId = "1";
        settings.CaptureDeviceName = "Blue Yeti";
        var model = new BantzModel(settings);

        Assert.Equal("1", model.ResolveCaptureDeviceId());
        Assert.Equal("none", model.InputDeviceListDisplay);
        Assert.Equal("flex", model.EmptyInputDevicesDisplay);
    }

    [Fact]
    public void CaptureFeedbackLeavesRoomInTheKeybindList()
    {
        var model = new BantzModel(AppSettings.Defaults());
        var normalHeight = model.BindingsListHeight;

        model.CaptureDisplay = "block";

        Assert.True(model.BindingsListHeight < normalHeight);
    }

    [Fact]
    public void AdvancedShortcutSettingsRoundTripThroughTheModel()
    {
        var model = new BantzModel(AppSettings.Defaults());
        var toggleBinding = new InputBinding
        {
            Device = InputDevice.Keyboard,
            Code = 0x7B,
            DisplayName = "F12",
        };

        model.ShortcutsEnabled = false;
        model.SetShortcutToggleBinding(toggleBinding);
        var settings = model.ToSettings();

        Assert.False(settings.ShortcutsEnabled);
        Assert.Equal("F12", settings.ShortcutToggleBinding?.DisplayName);
        Assert.Equal("Disabled", model.ShortcutStateLabel);
        Assert.Equal("Change", model.ShortcutToggleButtonLabel);
        Assert.Contains("disabled", model.ShortcutFooterText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("shortcuts-disabled", model.RecordShortcutClass);
    }

    [Fact]
    public void AdvancedDialogDoesNotResizeTheKeybindList()
    {
        var model = new BantzModel(AppSettings.Defaults());
        var normalHeight = model.BindingsListHeight;
        model.CaptureDisplay = "block";

        model.AdvancedBindingsExpanded = true;

        Assert.True(model.AdvancedBindingsExpanded);
        Assert.Equal(normalHeight, model.BindingsListHeight);
        Assert.Equal("none", model.PrimaryCaptureDisplay);
        Assert.Equal("flex", model.AdvancedCaptureDisplay);
    }

    [Fact]
    public void ShortcutExplanationCanBeExpanded()
    {
        var model = new BantzModel(AppSettings.Defaults());

        model.ShortcutInfoExpanded = true;

        Assert.Equal("block", model.ShortcutInfoDisplay);
        Assert.Equal("Hide explanation", model.ShortcutInfoLabel);
    }

}
