using System.Text.Json;

namespace Bantz.Settings;

public sealed class SettingsStore
{
    private readonly object _sync = new();
    private readonly Func<string> _pathProvider;

    public SettingsStore(string? path = null)
        : this(() => path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bantz",
            "settings.json"))
    {
    }

    public SettingsStore(Func<string> pathProvider)
    {
        _pathProvider = pathProvider;
    }

    public AppSettings Load()
    {
        lock (_sync)
        {
            var path = _pathProvider();
            try
            {
                if (!File.Exists(path))
                {
                    return AppSettings.Defaults();
                }

                using var stream = File.OpenRead(path);
                var settings = JsonSerializer.Deserialize(stream, AppSettingsJsonContext.Default.AppSettings)
                    ?? AppSettings.Defaults();
                Normalize(settings);
                return settings;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return AppSettings.Defaults();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_sync)
        {
            var path = _pathProvider();
            Normalize(settings);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = path + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, AppSettingsJsonContext.Default.AppSettings);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
    }

    private static void Normalize(AppSettings settings)
    {
        if (settings.Runtime.HasValue && !Enum.IsDefined(settings.Runtime.Value))
        {
            settings.Runtime = null;
        }

        settings.ButtonDelaySeconds = Math.Clamp(settings.ButtonDelaySeconds, 0, 10);
        settings.ShortcutDelaySeconds = Math.Clamp(settings.ShortcutDelaySeconds, 0, 10);
        settings.Bindings ??= [];
        settings.Bindings = settings.Bindings
            .Where(binding => binding.Code != 0)
            .GroupBy(binding => (binding.Device, binding.Code, binding.Modifiers))
            .Select(group => group.First())
            .ToList();
        if (settings.ShortcutToggleBinding?.Code == 0)
        {
            settings.ShortcutToggleBinding = null;
        }

        if (settings.ShortcutToggleBinding is { } toggleBinding)
        {
            settings.Bindings = settings.Bindings
                .Where(binding => !binding.SameInput(toggleBinding))
                .ToList();
        }
    }
}
