using System.Text.Json.Serialization;
using Bantz.Input;
using Bantz.Speech.Whisper;

namespace Bantz.Settings;

public sealed class AppSettings
{
    public TranscriptionRuntime? Runtime { get; set; }
    public bool AutoWrite { get; set; } = true;
    public bool AutoEnter { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool ButtonDelayEnabled { get; set; } = true;
    public int ButtonDelaySeconds { get; set; } = 5;
    public bool ShortcutDelayEnabled { get; set; }
    public int ShortcutDelaySeconds { get; set; }
    public bool ShortcutsEnabled { get; set; }
    public InputBinding? ShortcutToggleBinding { get; set; } = InputBinding.DefaultShortcutToggle();
    public List<InputBinding> Bindings { get; set; } = [InputBinding.DefaultKeyboard()];

    public static AppSettings Defaults() => new();
}

[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
