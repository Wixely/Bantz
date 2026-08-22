using System.ComponentModel;
using System.Runtime.InteropServices;
namespace Bantz.Input;

public sealed partial class WindowsHoldInputMonitor : IDisposable
{
    private const int KeyboardHook = 13;
    private const int MouseHook = 14;
    private const int KeyDown = 0x0100;
    private const int KeyUp = 0x0101;
    private const int SystemKeyDown = 0x0104;
    private const int SystemKeyUp = 0x0105;
    private const int LeftButtonDown = 0x0201;
    private const int LeftButtonUp = 0x0202;
    private const int RightButtonDown = 0x0204;
    private const int RightButtonUp = 0x0205;
    private const int MiddleButtonDown = 0x0207;
    private const int MiddleButtonUp = 0x0208;
    private const int ExtraButtonDown = 0x020B;
    private const int ExtraButtonUp = 0x020C;
    private const uint MouseLeft = 1;
    private const uint MouseRight = 2;
    private const uint MouseMiddle = 3;
    private const uint MouseBack = 4;
    private const uint MouseForward = 5;
    private const int VirtualKeyEscape = 0x1B;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyAlt = 0x12;
    private const int VirtualKeyLeftWindows = 0x5B;
    private const int VirtualKeyRightWindows = 0x5C;
    private const uint GamepadLeftTrigger = 1u << 16;
    private const uint GamepadRightTrigger = 1u << 17;
    private const byte TriggerThreshold = 30;

    private readonly object _sync = new();
    private readonly Func<IReadOnlyList<InputBinding>> _bindingsProvider;
    private readonly Func<bool> _shortcutsEnabledProvider;
    private readonly Func<InputBinding?> _shortcutToggleBindingProvider;
    private readonly HookProcedure _keyboardProcedure;
    private readonly HookProcedure _mouseProcedure;
    private readonly System.Threading.Timer _gamepadTimer;
    private readonly uint[] _previousGamepadButtons = new uint[4];
    private nint _keyboardHook;
    private nint _mouseHook;
    private InputBinding? _activeKeyboard;
    private InputBinding? _activeMouse;
    private (uint Controller, InputBinding Binding)? _activeGamepad;
    private InputBinding? _activeToggleKeyboard;
    private InputBinding? _activeToggleMouse;
    private (uint Controller, InputBinding Binding)? _activeToggleGamepad;
    private uint? _capturedMouseRelease;
    private bool _capturing;
    private bool _disposed;
    private int _gamepadPollActive;

    public WindowsHoldInputMonitor(
        Func<IReadOnlyList<InputBinding>> bindingsProvider,
        Func<bool> shortcutsEnabledProvider,
        Func<InputBinding?> shortcutToggleBindingProvider)
    {
        _bindingsProvider = bindingsProvider;
        _shortcutsEnabledProvider = shortcutsEnabledProvider;
        _shortcutToggleBindingProvider = shortcutToggleBindingProvider;
        _keyboardProcedure = KeyboardCallback;
        _mouseProcedure = MouseCallback;
        _keyboardHook = SetWindowsHookEx(KeyboardHook, _keyboardProcedure, nint.Zero, 0);
        _mouseHook = SetWindowsHookEx(MouseHook, _mouseProcedure, nint.Zero, 0);
        if (_keyboardHook == nint.Zero || _mouseHook == nint.Zero)
        {
            Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the global input hooks.");
        }

        _gamepadTimer = new System.Threading.Timer(PollGamepads, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(20));
    }

    public event Action? HotkeyPressed;
    public event Action? HotkeyReleased;
    public event Action? ShortcutTogglePressed;
    public event Action? LeftMouseReleased;
    public event Action<InputBinding>? BindingCaptured;
    public event Action? CaptureCancelled;

    public bool BeginCapture()
    {
        lock (_sync)
        {
            if (_capturing || _activeKeyboard is not null || _activeMouse is not null || _activeGamepad is not null ||
                _activeToggleKeyboard is not null || _activeToggleMouse is not null || _activeToggleGamepad is not null)
            {
                return false;
            }

            _capturing = true;
            return true;
        }
    }

    public void CancelCapture()
    {
        var cancelled = false;
        lock (_sync)
        {
            if (_capturing)
            {
                _capturing = false;
                cancelled = true;
            }
        }

        if (cancelled)
        {
            CaptureCancelled?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gamepadTimer?.Dispose();
        if (_keyboardHook != nint.Zero)
        {
            _ = UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = nint.Zero;
        }

        if (_mouseHook != nint.Zero)
        {
            _ = UnhookWindowsHookEx(_mouseHook);
            _mouseHook = nint.Zero;
        }

        _disposed = true;
    }

    private nint KeyboardCallback(int code, nuint message, nint data)
    {
        Action? notification = null;
        InputBinding? captured = null;
        var toggleShortcuts = false;
        var suppress = false;

        if (code >= 0)
        {
            var keyboard = Marshal.PtrToStructure<LowLevelKeyboardInput>(data);
            var virtualKey = keyboard.VirtualKey;
            var isDown = message is KeyDown or SystemKeyDown;
            var isUp = message is KeyUp or SystemKeyUp;

            lock (_sync)
            {
                if (_capturing && isDown && !IsModifier(virtualKey))
                {
                    _capturing = false;
                    suppress = true;
                    if (virtualKey == VirtualKeyEscape && CurrentModifiers() == KeyboardModifiers.None)
                    {
                        notification = CaptureCancelled;
                    }
                    else
                    {
                        captured = CreateKeyboardBinding(virtualKey, CurrentModifiers());
                    }
                }
                else if (_activeToggleKeyboard is { } activeToggle && virtualKey == activeToggle.Code)
                {
                    suppress = true;
                    if (isUp)
                    {
                        _activeToggleKeyboard = null;
                    }
                }
                else if (_activeKeyboard is { } active && virtualKey == active.Code)
                {
                    suppress = true;
                    if (isUp)
                    {
                        _activeKeyboard = null;
                        notification = HotkeyReleased;
                    }
                }
                else if (isDown && !IsModifier(virtualKey))
                {
                    var modifiers = CurrentModifiers();
                    var toggleBinding = GetShortcutToggleBinding();
                    if (toggleBinding is not null &&
                        toggleBinding.Device == InputDevice.Keyboard &&
                        toggleBinding.Code == virtualKey &&
                        toggleBinding.Modifiers == modifiers)
                    {
                        _activeToggleKeyboard = toggleBinding;
                        suppress = true;
                        toggleShortcuts = true;
                    }
                    else if (GetShortcutsEnabled())
                    {
                        var binding = GetBindings().FirstOrDefault(candidate =>
                            candidate.Device == InputDevice.Keyboard &&
                            candidate.Code == virtualKey &&
                            candidate.Modifiers == modifiers);
                        if (binding is not null)
                        {
                            _activeKeyboard = binding;
                            suppress = true;
                            notification = HotkeyPressed;
                        }
                    }
                }
            }
        }

        if (captured is not null)
        {
            BindingCaptured?.Invoke(captured);
        }
        else
        {
            if (toggleShortcuts)
            {
                ShortcutTogglePressed?.Invoke();
            }

            notification?.Invoke();
        }

        return suppress ? 1 : CallNextHookEx(nint.Zero, code, message, data);
    }

    private nint MouseCallback(int code, nuint message, nint data)
    {
        if (code < 0)
        {
            return CallNextHookEx(nint.Zero, code, message, data);
        }

        var mouse = Marshal.PtrToStructure<LowLevelMouseInput>(data);
        if (!TryMouseButton(message, mouse.MouseData, out var mouseCode, out var isDown, out var isUp))
        {
            if (message == LeftButtonUp)
            {
                LeftMouseReleased?.Invoke();
            }

            return CallNextHookEx(nint.Zero, code, message, data);
        }

        Action? notification = null;
        InputBinding? captured = null;
        var toggleShortcuts = false;
        var suppress = false;
        var overCurrentProcess = IsCurrentProcessWindowAt(mouse.Point);
        lock (_sync)
        {
            if (_capturedMouseRelease == mouseCode)
            {
                suppress = true;
                if (isUp)
                {
                    _capturedMouseRelease = null;
                }
            }
            else if (_activeToggleMouse is { } activeToggle && activeToggle.Code == mouseCode)
            {
                suppress = true;
                if (isUp)
                {
                    _activeToggleMouse = null;
                }
            }
            else if (_activeMouse is { } active && active.Code == mouseCode)
            {
                suppress = true;
                if (isUp)
                {
                    _activeMouse = null;
                    notification = HotkeyReleased;
                }
            }
            else if (!overCurrentProcess && _capturing && isDown)
            {
                _capturing = false;
                _capturedMouseRelease = mouseCode;
                captured = CreateMouseBinding(mouseCode, CurrentModifiers());
                suppress = true;
            }
            else if (!overCurrentProcess && isDown)
            {
                var modifiers = CurrentModifiers();
                var toggleBinding = GetShortcutToggleBinding();
                if (toggleBinding is not null &&
                    toggleBinding.Device == InputDevice.Mouse &&
                    toggleBinding.Code == mouseCode &&
                    toggleBinding.Modifiers == modifiers)
                {
                    _activeToggleMouse = toggleBinding;
                    suppress = true;
                    toggleShortcuts = true;
                }
                else if (GetShortcutsEnabled())
                {
                    var binding = GetBindings().FirstOrDefault(candidate =>
                        candidate.Device == InputDevice.Mouse &&
                        candidate.Code == mouseCode &&
                        candidate.Modifiers == modifiers);
                    if (binding is not null)
                    {
                        _activeMouse = binding;
                        suppress = true;
                        notification = HotkeyPressed;
                    }
                }
            }
        }

        if (captured is not null)
        {
            BindingCaptured?.Invoke(captured);
        }
        else
        {
            if (toggleShortcuts)
            {
                ShortcutTogglePressed?.Invoke();
            }

            notification?.Invoke();
        }

        return suppress ? 1 : CallNextHookEx(nint.Zero, code, message, data);
    }

    private void PollGamepads(object? state)
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _gamepadPollActive, 1) != 0)
        {
            return;
        }

        try
        {
            for (uint controller = 0; controller < _previousGamepadButtons.Length; controller++)
            {
                if (XInputGetState(controller, out var inputState) != 0)
                {
                    _previousGamepadButtons[controller] = 0;
                    continue;
                }

                var buttons = (uint)inputState.Gamepad.Buttons;
                if (inputState.Gamepad.LeftTrigger > TriggerThreshold) buttons |= GamepadLeftTrigger;
                if (inputState.Gamepad.RightTrigger > TriggerThreshold) buttons |= GamepadRightTrigger;
                var newlyPressed = buttons & ~_previousGamepadButtons[controller];

                Action? notification = null;
                InputBinding? captured = null;
                var toggleShortcuts = false;
                lock (_sync)
                {
                    if (_capturing && newlyPressed != 0)
                    {
                        var code = LowestSetBit(newlyPressed);
                        _capturing = false;
                        captured = new InputBinding
                        {
                            Device = InputDevice.Gamepad,
                            Code = code,
                            DisplayName = GamepadName(code),
                        };
                    }
                    else if (_activeToggleGamepad is { } activeToggle && activeToggle.Controller == controller &&
                             (buttons & activeToggle.Binding.Code) == 0)
                    {
                        _activeToggleGamepad = null;
                    }
                    else if (_activeGamepad is { } active && active.Controller == controller &&
                             (buttons & active.Binding.Code) == 0)
                    {
                        _activeGamepad = null;
                        notification = HotkeyReleased;
                    }
                    else if (_activeGamepad is null && _activeToggleGamepad is null && newlyPressed != 0)
                    {
                        var toggleBinding = GetShortcutToggleBinding();
                        if (toggleBinding is not null &&
                            toggleBinding.Device == InputDevice.Gamepad &&
                            (newlyPressed & toggleBinding.Code) != 0)
                        {
                            _activeToggleGamepad = (controller, toggleBinding);
                            toggleShortcuts = true;
                        }
                        else if (GetShortcutsEnabled())
                        {
                            var binding = GetBindings().FirstOrDefault(candidate =>
                                candidate.Device == InputDevice.Gamepad && (newlyPressed & candidate.Code) != 0);
                            if (binding is not null)
                            {
                                _activeGamepad = (controller, binding);
                                notification = HotkeyPressed;
                            }
                        }
                    }
                }

                _previousGamepadButtons[controller] = buttons;
                if (captured is not null)
                {
                    BindingCaptured?.Invoke(captured);
                }
                else
                {
                    if (toggleShortcuts)
                    {
                        ShortcutTogglePressed?.Invoke();
                    }

                    notification?.Invoke();
                }
            }
        }
        finally
        {
            Volatile.Write(ref _gamepadPollActive, 0);
        }
    }

    private IReadOnlyList<InputBinding> GetBindings()
    {
        try
        {
            return _bindingsProvider() ?? [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    private bool GetShortcutsEnabled()
    {
        try
        {
            return _shortcutsEnabledProvider();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private InputBinding? GetShortcutToggleBinding()
    {
        try
        {
            return _shortcutToggleBindingProvider()?.Copy();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static InputBinding CreateKeyboardBinding(uint virtualKey, KeyboardModifiers modifiers) => new()
    {
        Device = InputDevice.Keyboard,
        Code = virtualKey,
        Modifiers = modifiers,
        DisplayName = KeyboardName(virtualKey, modifiers),
    };

    private static InputBinding CreateMouseBinding(uint code, KeyboardModifiers modifiers) => new()
    {
        Device = InputDevice.Mouse,
        Code = code,
        Modifiers = modifiers,
        DisplayName = MouseName(code, modifiers),
    };

    private static KeyboardModifiers CurrentModifiers()
    {
        var result = KeyboardModifiers.None;
        if (IsPressed(VirtualKeyControl)) result |= KeyboardModifiers.Control;
        if (IsPressed(VirtualKeyShift)) result |= KeyboardModifiers.Shift;
        if (IsPressed(VirtualKeyAlt)) result |= KeyboardModifiers.Alt;
        if (IsPressed(VirtualKeyLeftWindows) || IsPressed(VirtualKeyRightWindows)) result |= KeyboardModifiers.Windows;
        return result;
    }

    private static string KeyboardName(uint virtualKey, KeyboardModifiers modifiers)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(KeyboardModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyboardModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyboardModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyboardModifiers.Windows)) parts.Add("Win");
        parts.Add(KeyName(virtualKey));
        return string.Join(" + ", parts);
    }

    private static string KeyName(uint virtualKey) => virtualKey switch
    {
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Escape",
        0x20 => "Space",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2D => "Insert",
        0x2E => "Delete",
        >= 0x30 and <= 0x5A => ((char)virtualKey).ToString(),
        >= 0x70 and <= 0x87 => $"F{virtualKey - 0x6F}",
        _ => $"Key 0x{virtualKey:X2}",
    };

    private static string GamepadName(uint code) => code switch
    {
        0x0001 => "Gamepad D-pad Up",
        0x0002 => "Gamepad D-pad Down",
        0x0004 => "Gamepad D-pad Left",
        0x0008 => "Gamepad D-pad Right",
        0x0010 => "Gamepad Start",
        0x0020 => "Gamepad Back",
        0x0040 => "Gamepad Left Stick",
        0x0080 => "Gamepad Right Stick",
        0x0100 => "Gamepad Left Shoulder",
        0x0200 => "Gamepad Right Shoulder",
        0x1000 => "Gamepad A",
        0x2000 => "Gamepad B",
        0x4000 => "Gamepad X",
        0x8000 => "Gamepad Y",
        GamepadLeftTrigger => "Gamepad Left Trigger",
        GamepadRightTrigger => "Gamepad Right Trigger",
        _ => $"Gamepad 0x{code:X}",
    };

    private static string MouseName(uint code, KeyboardModifiers modifiers)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(KeyboardModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyboardModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyboardModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyboardModifiers.Windows)) parts.Add("Win");
        parts.Add(code switch
        {
            MouseLeft => "Mouse Left Button",
            MouseRight => "Mouse Right Button",
            MouseMiddle => "Mouse Wheel Button",
            MouseBack => "Mouse Back Button",
            MouseForward => "Mouse Forward Button",
            _ => $"Mouse Button {code}",
        });
        return string.Join(" + ", parts);
    }

    private static bool TryMouseButton(
        nuint message,
        uint mouseData,
        out uint mouseCode,
        out bool isDown,
        out bool isUp)
    {
        isDown = message is LeftButtonDown or RightButtonDown or MiddleButtonDown or ExtraButtonDown;
        isUp = message is LeftButtonUp or RightButtonUp or MiddleButtonUp or ExtraButtonUp;
        mouseCode = message switch
        {
            LeftButtonDown or LeftButtonUp => MouseLeft,
            RightButtonDown or RightButtonUp => MouseRight,
            MiddleButtonDown or MiddleButtonUp => MouseMiddle,
            ExtraButtonDown or ExtraButtonUp when (mouseData >> 16) == 1 => MouseBack,
            ExtraButtonDown or ExtraButtonUp when (mouseData >> 16) == 2 => MouseForward,
            _ => 0,
        };
        return mouseCode != 0;
    }

    private static bool IsCurrentProcessWindowAt(NativePoint point)
    {
        var window = WindowFromPoint(point);
        if (window == nint.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    private static uint LowestSetBit(uint value) => value & (uint)-(int)value;
    private static bool IsPressed(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    private static bool IsModifier(uint virtualKey) => virtualKey is
        0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private delegate nint HookProcedure(int code, nuint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LowLevelKeyboardInput
    {
        public readonly uint VirtualKey;
        public readonly uint ScanCode;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LowLevelMouseInput
    {
        public readonly NativePoint Point;
        public readonly uint MouseData;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct XInputState
    {
        public readonly uint PacketNumber;
        public readonly XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct XInputGamepad
    {
        public readonly ushort Buttons;
        public readonly byte LeftTrigger;
        public readonly byte RightTrigger;
        public readonly short ThumbLeftX;
        public readonly short ThumbLeftY;
        public readonly short ThumbRightX;
        public readonly short ThumbRightY;
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static partial nint SetWindowsHookEx(int hookId, HookProcedure procedure, nint module, uint threadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hook, int code, nuint message, nint data);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(NativePoint point);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static partial uint XInputGetState(uint userIndex, out XInputState state);
}
