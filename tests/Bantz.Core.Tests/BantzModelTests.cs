using Bantz.Settings;
using Bantz.Input;
using Bantz.Ui;
using Xunit;

namespace Bantz.Core.Tests;

public sealed class BantzModelTests
{
    [Theory]
    [InlineData("settings", "flex", "selected", "", "", "")]
    [InlineData("keybinds", "flex", "", "selected", "", "")]
    [InlineData("diagnostics", "flex", "", "", "selected", "")]
    [InlineData("about", "flex", "", "", "", "selected")]
    [InlineData("main", "none", "", "", "", "")]
    public void ConfigurationTabsExposeOneSelectedPage(
        string page,
        string configDisplay,
        string settingsClass,
        string keybindsClass,
        string diagnosticsClass,
        string aboutClass)
    {
        var model = new BantzModel(AppSettings.Defaults()) { Page = page };

        Assert.Equal(configDisplay, model.ConfigDisplay);
        Assert.Equal(settingsClass, model.SettingsTabClass);
        Assert.Equal(keybindsClass, model.KeybindsTabClass);
        Assert.Equal(diagnosticsClass, model.DiagnosticsTabClass);
        Assert.Equal(aboutClass, model.AboutTabClass);
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
