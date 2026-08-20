using System.Text.Json.Serialization;

namespace Bantz.Settings;

[Flags]
public enum KeyboardModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Windows = 8,
}

public enum InputDevice
{
    Keyboard,
    Gamepad,
    Mouse,
}

public enum TranscriptionRuntime
{
    Automatic,
    Cpu,
}

public sealed class InputBinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public InputDevice Device { get; set; }
    public uint Code { get; set; }
    public KeyboardModifiers Modifiers { get; set; }
    public string DisplayName { get; set; } = "Unassigned";

    public InputBinding Copy() => new()
    {
        Id = Id,
        Device = Device,
        Code = Code,
        Modifiers = Modifiers,
        DisplayName = DisplayName,
    };

    public bool SameInput(InputBinding other) =>
        Device == other.Device && Code == other.Code && Modifiers == other.Modifiers;

    public static InputBinding DefaultKeyboard() => new()
    {
        Id = "default-ctrl-shift-space",
        Device = InputDevice.Keyboard,
        Code = 0x20,
        Modifiers = KeyboardModifiers.Control | KeyboardModifiers.Shift,
        DisplayName = "Ctrl + Shift + Space",
    };
}

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
    public List<InputBinding> Bindings { get; set; } = [InputBinding.DefaultKeyboard()];

    public static AppSettings Defaults() => new();
}

[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
