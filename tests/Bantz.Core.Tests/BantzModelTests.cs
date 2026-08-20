using Bantz.Settings;
using Bantz.Ui;
using Xunit;

namespace Bantz.Core.Tests;

public sealed class BantzModelTests
{
    [Theory]
    [InlineData("settings", "flex", "selected", "", "")]
    [InlineData("keybinds", "flex", "", "selected", "")]
    [InlineData("diagnostics", "flex", "", "", "selected")]
    [InlineData("main", "none", "", "", "")]
    public void ConfigurationTabsExposeOneSelectedPage(
        string page,
        string configDisplay,
        string settingsClass,
        string keybindsClass,
        string diagnosticsClass)
    {
        var model = new BantzModel(AppSettings.Defaults()) { Page = page };

        Assert.Equal(configDisplay, model.ConfigDisplay);
        Assert.Equal(settingsClass, model.SettingsTabClass);
        Assert.Equal(keybindsClass, model.KeybindsTabClass);
        Assert.Equal(diagnosticsClass, model.DiagnosticsTabClass);
    }

    [Fact]
    public void CaptureFeedbackLeavesRoomInTheKeybindList()
    {
        var model = new BantzModel(AppSettings.Defaults());
        var normalHeight = model.BindingsListHeight;

        model.CaptureDisplay = "block";

        Assert.True(model.BindingsListHeight < normalHeight);
    }
}
