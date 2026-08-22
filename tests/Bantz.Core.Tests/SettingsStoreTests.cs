using Bantz.Settings;
using Bantz.Input;
using Bantz.Speech.Whisper;
using Xunit;

namespace Bantz.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bantz-tests-{Guid.NewGuid():N}");
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void NewInstallRequiresARuntimeChoice()
    {
        var settings = new SettingsStore(SettingsPath).Load();

        Assert.Null(settings.Runtime);
        Assert.True(settings.AutoWrite);
        Assert.True(settings.ButtonDelayEnabled);
        Assert.Equal(5, settings.ButtonDelaySeconds);
        Assert.False(settings.ShortcutsEnabled);
        Assert.Equal("Ctrl + T", settings.ShortcutToggleBinding?.DisplayName);
    }

    [Fact]
    public void AutomaticallyWriteChoiceSurvivesRestart()
    {
        var store = new SettingsStore(SettingsPath);
        var settings = AppSettings.Defaults();
        settings.AutoWrite = false;

        store.Save(settings);
        var reloaded = store.Load();

        Assert.False(reloaded.AutoWrite);
    }

    [Fact]
    public void SettingsFromBeforeAutoWriteDefaultToEnabled()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{\"AutoEnter\":false,\"ButtonDelaySeconds\":5}");

        var settings = new SettingsStore(SettingsPath).Load();

        Assert.True(settings.AutoWrite);
        Assert.False(settings.ShortcutsEnabled);
    }

    [Fact]
    public void GpuPreferredChoiceSurvivesRestart()
    {
        var store = new SettingsStore(SettingsPath);
        var settings = AppSettings.Defaults();
        settings.Runtime = TranscriptionRuntime.Automatic;

        store.Save(settings);
        var reloaded = new SettingsStore(SettingsPath).Load();

        Assert.Equal(TranscriptionRuntime.Automatic, reloaded.Runtime);
    }

    [Fact]
    public void AlwaysOnTopChoiceSurvivesRestart()
    {
        var store = new SettingsStore(SettingsPath);
        var settings = AppSettings.Defaults();
        settings.AlwaysOnTop = true;

        store.Save(settings);
        var reloaded = store.Load();

        Assert.True(reloaded.AlwaysOnTop);
    }

    [Fact]
    public void SavedSettingsAreClampedAndDuplicateBindingsAreRemoved()
    {
        var store = new SettingsStore(SettingsPath);
        var settings = AppSettings.Defaults();
        settings.ButtonDelaySeconds = 99;
        settings.ShortcutDelaySeconds = -3;
        settings.Bindings.Add(InputBinding.DefaultKeyboard());

        store.Save(settings);
        var reloaded = store.Load();

        Assert.Equal(10, reloaded.ButtonDelaySeconds);
        Assert.Equal(0, reloaded.ShortcutDelaySeconds);
        Assert.Single(reloaded.Bindings);
    }

    [Fact]
    public void MouseBindingsSurviveRestartAndDeduplicate()
    {
        var store = new SettingsStore(SettingsPath);
        var settings = AppSettings.Defaults();
        var mouse = new InputBinding
        {
            Device = InputDevice.Mouse,
            Code = 3,
            DisplayName = "Mouse Wheel Button",
        };
        settings.Bindings = [mouse, mouse.Copy()];

        store.Save(settings);
        var reloaded = store.Load();

        var binding = Assert.Single(reloaded.Bindings);
        Assert.Equal(InputDevice.Mouse, binding.Device);
        Assert.Equal(3u, binding.Code);
        Assert.Equal("Mouse Wheel Button", binding.DisplayName);
    }

    [Fact]
    public void ShortcutToggleBindingAndEnabledStateSurviveRestart()
    {
        var store = new SettingsStore(SettingsPath);
        var settings = AppSettings.Defaults();
        settings.ShortcutsEnabled = false;
        settings.ShortcutToggleBinding = new InputBinding
        {
            Device = InputDevice.Keyboard,
            Code = 0x7B,
            DisplayName = "F12",
        };

        store.Save(settings);
        var reloaded = store.Load();

        Assert.False(reloaded.ShortcutsEnabled);
        Assert.Equal("F12", reloaded.ShortcutToggleBinding?.DisplayName);
    }

    [Fact]
    public void RemovedShortcutToggleBindingStaysRemoved()
    {
        var store = new SettingsStore(SettingsPath);
        var settings = AppSettings.Defaults();
        settings.ShortcutToggleBinding = null;

        store.Save(settings);
        var reloaded = store.Load();

        Assert.Null(reloaded.ShortcutToggleBinding);
    }

    [Fact]
    public void ShortcutToggleBindingTakesPriorityOverDuplicatePttBinding()
    {
        var store = new SettingsStore(SettingsPath);
        var settings = AppSettings.Defaults();
        settings.ShortcutToggleBinding = InputBinding.DefaultKeyboard();

        store.Save(settings);
        var reloaded = store.Load();

        Assert.Empty(reloaded.Bindings);
        Assert.NotNull(reloaded.ShortcutToggleBinding);
    }

    public void Dispose()
    {
        if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
        if (File.Exists(SettingsPath + ".tmp")) File.Delete(SettingsPath + ".tmp");
        if (Directory.Exists(_directory)) Directory.Delete(_directory);
    }
}
