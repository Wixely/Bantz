namespace Bantz.Input;

/// <summary>
/// The kernel's input codes, as they appear in evdev events, and what they mean to a binding.
/// Codes are from <c>linux/input-event-codes.h</c>; they are not the virtual-key numbers Windows
/// uses, so a binding captured on one platform names a different input on the other.
/// </summary>
public static class EvdevCodes
{
    /// <summary>An <c>EV_KEY</c> event: a key or button changed state.</summary>
    public const ushort EventKey = 0x01;

    private const int MouseFirst = 0x110;
    private const int MouseLast = 0x117;
    private const int JoystickFirst = 0x120;
    private const int GamepadLast = 0x13f;
    private const int DigitiserFirst = 0x140;
    private const int DigitiserLast = 0x14f;
    private const int TriggerHappyFirst = 0x2c0;
    private const int TriggerHappyLast = 0x2ff;

    /// <summary>BTN_LEFT — the click that chooses a destination window.</summary>
    public const int ButtonLeft = 0x110;

    private static readonly Dictionary<int, KeyboardModifiers> Modifiers = new()
    {
        [29] = KeyboardModifiers.Control,   // KEY_LEFTCTRL
        [97] = KeyboardModifiers.Control,   // KEY_RIGHTCTRL
        [42] = KeyboardModifiers.Shift,     // KEY_LEFTSHIFT
        [54] = KeyboardModifiers.Shift,     // KEY_RIGHTSHIFT
        [56] = KeyboardModifiers.Alt,       // KEY_LEFTALT
        [100] = KeyboardModifiers.Alt,      // KEY_RIGHTALT
        [125] = KeyboardModifiers.Windows,  // KEY_LEFTMETA
        [126] = KeyboardModifiers.Windows,  // KEY_RIGHTMETA
    };

    private static readonly Dictionary<int, string> Names = new()
    {
        // Gamepad, named as the buttons are labelled rather than as the kernel spells them: a
        // Steam Deck's A is BTN_SOUTH.
        [0x130] = "A",
        [0x131] = "B",
        [0x133] = "X",
        [0x134] = "Y",
        [0x136] = "L1",
        [0x137] = "R1",
        [0x138] = "L2",
        [0x139] = "R2",
        [0x13a] = "Select",
        [0x13b] = "Start",
        [0x13c] = "Guide",
        [0x13d] = "L3",
        [0x13e] = "R3",
        [0x220] = "D-pad up",
        [0x221] = "D-pad down",
        [0x222] = "D-pad left",
        [0x223] = "D-pad right",

        // Mouse
        [0x110] = "Left click",
        [0x111] = "Right click",
        [0x112] = "Middle click",
        [0x113] = "Mouse 4",
        [0x114] = "Mouse 5",

        // Keys worth naming; anything else falls back to its number.
        [1] = "Esc",
        [14] = "Backspace",
        [15] = "Tab",
        [28] = "Enter",
        [57] = "Space",
        [29] = "Ctrl",
        [42] = "Shift",
        [56] = "Alt",
        [125] = "Super",
        [97] = "Right Ctrl",
        [54] = "Right Shift",
        [100] = "Right Alt",
        [126] = "Right Super",
        [59] = "F1",
        [60] = "F2",
        [61] = "F3",
        [62] = "F4",
        [63] = "F5",
        [64] = "F6",
        [65] = "F7",
        [66] = "F8",
        [67] = "F9",
        [68] = "F10",
        [87] = "F11",
        [88] = "F12",
        [103] = "Up",
        [108] = "Down",
        [105] = "Left",
        [106] = "Right",
    };

    private static readonly string Letters = "  1234567890-=  qwertyuiop[]  asdfghjkl;'`  \\zxcvbnm,./";

    /// <summary>Which kind of binding a code belongs to, or null for codes bindings ignore.</summary>
    public static InputDevice? DeviceFor(int code) => code switch
    {
        >= MouseFirst and <= MouseLast => InputDevice.Mouse,
        >= JoystickFirst and <= GamepadLast => InputDevice.Gamepad,
        >= TriggerHappyFirst and <= TriggerHappyLast => InputDevice.Gamepad,
        // The digitiser range is a touchscreen reporting contact, not a button anyone binds.
        >= DigitiserFirst and <= DigitiserLast => null,
        > 0 and < 0x100 => InputDevice.Keyboard,
        _ => null,
    };

    /// <summary>The modifier this code is, or None when it is an ordinary key.</summary>
    public static KeyboardModifiers ModifierFor(int code) =>
        Modifiers.TryGetValue(code, out var modifier) ? modifier : KeyboardModifiers.None;

    /// <summary>A name for a binding, e.g. "Ctrl + Shift + Space" or "Gamepad A".</summary>
    public static string DescribeBinding(InputDevice device, int code, KeyboardModifiers modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(KeyboardModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyboardModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyboardModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyboardModifiers.Windows)) parts.Add("Super");

        var name = DescribeCode(code);
        parts.Add(device == InputDevice.Gamepad ? $"Gamepad {name}" : name);
        return string.Join(" + ", parts);
    }

    private static string DescribeCode(int code)
    {
        if (Names.TryGetValue(code, out var name))
        {
            return name;
        }

        if (code > 0 && code < Letters.Length && Letters[code] != ' ')
        {
            return char.ToUpperInvariant(Letters[code]).ToString();
        }

        return DeviceFor(code) == InputDevice.Gamepad ? $"Button {code - 0x12f}" : $"Key {code}";
    }
}
