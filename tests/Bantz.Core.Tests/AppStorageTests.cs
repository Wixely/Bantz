using Bantz.Settings;
using Xunit;

namespace Bantz.Core.Tests;

public sealed class AppStorageTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), $"bantz-storage-tests-{Guid.NewGuid():N}");

    [Fact]
    public void FreshInstallRequiresAStorageChoice()
    {
        var storage = CreateStorage();

        Assert.Equal(StorageMode.Unselected, storage.Mode);
        Assert.False(storage.IsSelected);
    }

    [Fact]
    public void PortableSelectionKeepsEveryManagedPathTogether()
    {
        var storage = CreateStorage();
        storage.Select(StorageMode.Portable);

        var expectedRoot = Path.Combine(_testRoot, "app", "BantzData");
        Assert.Equal(expectedRoot, storage.Root);
        Assert.StartsWith(expectedRoot, storage.SettingsPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(expectedRoot, storage.ModelPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(expectedRoot, storage.RuntimeRoot, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(expectedRoot, "storage.json")));
        Assert.Equal(StorageMode.Portable, CreateStorage().Mode);
    }

    [Fact]
    public void PerUserSelectionIsRemembered()
    {
        var storage = CreateStorage();
        storage.Select(StorageMode.PerUser);

        Assert.Equal(Path.Combine(_testRoot, "user"), storage.Root);
        Assert.Equal(StorageMode.PerUser, CreateStorage().Mode);
    }

    [Fact]
    public void ExistingUserDataIsAdoptedWithoutPromptingAgain()
    {
        var userRoot = Path.Combine(_testRoot, "user");
        Directory.CreateDirectory(userRoot);
        File.WriteAllText(Path.Combine(userRoot, "settings.json"), "{}");

        var storage = CreateStorage();

        Assert.Equal(StorageMode.PerUser, storage.Mode);
        Assert.True(File.Exists(Path.Combine(userRoot, "storage.json")));
    }

    private AppStorage CreateStorage() => new(
        Path.Combine(_testRoot, "app"),
        Path.Combine(_testRoot, "user"));

    public void Dispose()
    {
        if (!Directory.Exists(_testRoot)) return;

        foreach (var file in Directory.GetFiles(_testRoot, "*", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }

        foreach (var directory in Directory.GetDirectories(_testRoot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            Directory.Delete(directory);
        }

        Directory.Delete(_testRoot);
    }
}
