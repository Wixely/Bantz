namespace Bantz.Input;

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

/// <summary>A keyboard, mouse, or XInput binding.</summary>
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

    public static InputBinding DefaultShortcutToggle() => new()
    {
        Id = "default-ctrl-t-shortcut-toggle",
        Device = InputDevice.Keyboard,
        Code = 0x54,
        Modifiers = KeyboardModifiers.Control,
        DisplayName = "Ctrl + T",
    };
}

/// <summary>Platform support advertised before hooks are installed.</summary>
public sealed record GlobalInputCapabilities(
    bool SupportsGlobalBindings,
    bool SupportsKeyboard,
    bool SupportsMouse,
    bool SupportsGamepad)
{
    public static GlobalInputCapabilities Current => OperatingSystem.IsWindows() || OperatingSystem.IsLinux()
        ? new(true, true, true, true)
        : new(false, false, false, false);
}
