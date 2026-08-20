using Bantz.Settings;
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
        Assert.True(settings.ButtonDelayEnabled);
        Assert.Equal(5, settings.ButtonDelaySeconds);
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

    public void Dispose()
    {
        if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
        if (File.Exists(SettingsPath + ".tmp")) File.Delete(SettingsPath + ".tmp");
        if (Directory.Exists(_directory)) Directory.Delete(_directory);
    }
}
