using System.ComponentModel;
using System.Runtime.InteropServices;
using Bantz.Core;

namespace Bantz.Platform.Windows;

public sealed partial class WindowsTextInjector : ITextInjector
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const ushort VirtualKeyReturn = 0x0D;

    public TextInjectionResult InjectIntoForeground(string text, bool pressEnter)
    {
        var foreground = GetForegroundWindow();
        if (foreground == nint.Zero)
        {
            return TextInjectionResult.Failure("No destination window is active. Your transcript is still shown in Bantz.");
        }

        _ = GetWindowThreadProcessId(foreground, out var processId);
        if (processId == (uint)Environment.ProcessId)
        {
            return TextInjectionResult.Failure("Choose a text field in another app. Your transcript is still shown in Bantz.");
        }

        try
        {
            var inputs = new List<Input>(text.Length * 2 + (pressEnter ? 2 : 0));
            foreach (var character in text)
            {
                inputs.Add(UnicodeInput(character, keyUp: false));
                inputs.Add(UnicodeInput(character, keyUp: true));
            }

            if (pressEnter)
            {
                inputs.Add(VirtualKeyInput(VirtualKeyReturn, keyUp: false));
                inputs.Add(VirtualKeyInput(VirtualKeyReturn, keyUp: true));
            }

            for (var offset = 0; offset < inputs.Count; offset += 256)
            {
                var batch = inputs.GetRange(offset, Math.Min(256, inputs.Count - offset)).ToArray();
                var sent = SendInput((uint)batch.Length, batch, Marshal.SizeOf<Input>());
                if (sent != batch.Length)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }

            return TextInjectionResult.Success();
        }
        catch (Exception exception) when (exception is Win32Exception or OverflowException)
        {
            return TextInjectionResult.Failure($"Windows could not type the transcript: {exception.Message}");
        }
    }

    private static Input UnicodeInput(char character, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                Scan = character,
                Flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0),
            },
        },
    };

    private static Input VirtualKeyInput(ushort virtualKey, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? KeyEventKeyUp : 0,
            },
        },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint inputCount, [In] Input[] inputs, int inputSize);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);
}
