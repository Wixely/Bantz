using System.Text.Json.Serialization;
using Bantz.Input;
using Bantz.Speech.Whisper;

namespace Bantz.Settings;

public sealed class AppSettings
{
    public TranscriptionRuntime? Runtime { get; set; }
    public bool AutoWrite { get; set; } = true;
    public bool AutoEnter { get; set; }

    /// <summary>
    /// Delivers the transcript by clipboard paste instead of synthesized keystrokes, for
    /// destinations that drop typed Unicode such as Remote Desktop sessions.
    /// </summary>
    public bool ClipboardPaste { get; set; }

    public bool AlwaysOnTop { get; set; }
    public bool ButtonDelayEnabled { get; set; } = true;
    public int ButtonDelaySeconds { get; set; } = 5;
    public bool ShortcutDelayEnabled { get; set; }
    public int ShortcutDelaySeconds { get; set; }
    public bool ShortcutsEnabled { get; set; }

    /// <summary>The chosen capture device id, or null to follow the operating system default.</summary>
    public string? CaptureDeviceId { get; set; }

    /// <summary>
    /// The chosen device's name when it was picked. Windows wave-in ids are positional, so the
    /// name is what re-finds the same microphone after devices are added or removed.
    /// </summary>
    public string? CaptureDeviceName { get; set; }

    public InputBinding? ShortcutToggleBinding { get; set; } = InputBinding.DefaultShortcutToggle();
    public List<InputBinding> Bindings { get; set; } = [InputBinding.DefaultKeyboard()];

    public static AppSettings Defaults() => new();
}

[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
