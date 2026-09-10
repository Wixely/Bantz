namespace Bantz.Settings;

public enum StorageMode
{
    Unselected,
    PerUser,
    Portable,
}

public sealed class AppStorage
{
    private const string MetadataFileName = "storage.json";
    private readonly string _portableRoot;
    private readonly string _userRoot;

    public AppStorage(string? executableDirectory = null, string? userRoot = null)
    {
        executableDirectory ??= Path.GetDirectoryName(Environment.ProcessPath)
            ?? AppContext.BaseDirectory;
        _portableRoot = Path.Combine(Path.GetFullPath(executableDirectory), "BantzData");
        _userRoot = Path.GetFullPath(userRoot ?? DefaultUserRoot());
        Mode = DetectMode();

        if (Mode == StorageMode.PerUser && !File.Exists(MetadataPath(_userRoot)))
        {
            WriteMetadata(_userRoot, StorageMode.PerUser);
        }
    }

    public StorageMode Mode { get; private set; }
    public bool IsSelected => Mode != StorageMode.Unselected;
    public string Root => Mode == StorageMode.Portable ? _portableRoot : _userRoot;
    public string SettingsPath => Path.Combine(Root, "settings.json");
    /// <summary>The folder downloaded speech models are kept in.</summary>
    public string ModelsRoot => Path.Combine(Root, "models");
    public string ModelPath => Path.Combine(ModelsRoot, "ggml-base.en.bin");
    public string RuntimeRoot => Path.Combine(Root, "runtimes");
    public string DisplayPath => Root;

    public void Select(StorageMode mode)
    {
        if (mode is not StorageMode.PerUser and not StorageMode.Portable)
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        var root = mode == StorageMode.Portable ? _portableRoot : _userRoot;
        WriteMetadata(root, mode);
        Mode = mode;
    }

    private StorageMode DetectMode()
    {
        if (File.Exists(MetadataPath(_portableRoot)))
        {
            return StorageMode.Portable;
        }

        if (File.Exists(MetadataPath(_userRoot)) || HasLegacyUserData())
        {
            return StorageMode.PerUser;
        }

        return StorageMode.Unselected;
    }

    private bool HasLegacyUserData() =>
        File.Exists(Path.Combine(_userRoot, "settings.json")) ||
        Directory.Exists(Path.Combine(_userRoot, "models")) ||
        Directory.Exists(Path.Combine(_userRoot, "runtimes"));

    private static void WriteMetadata(string root, StorageMode mode)
    {
        Directory.CreateDirectory(root);
        var metadataPath = MetadataPath(root);
        var temporaryPath = metadataPath + ".tmp";
        File.WriteAllText(
            temporaryPath,
            $$"""
            {
              "version": 1,
              "mode": "{{mode}}"
            }
            """);
        File.Move(temporaryPath, metadataPath, overwrite: true);
    }

    private static string MetadataPath(string root) => Path.Combine(root, MetadataFileName);

    private static string DefaultUserRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdgData))
            {
                return Path.Combine(xdgData, "bantz");
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bantz");
    }
}
