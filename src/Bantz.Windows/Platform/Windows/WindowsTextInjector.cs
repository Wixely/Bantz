using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Bantz.Core;

namespace Bantz.Platform.Windows;

public sealed partial class WindowsTextInjector : ITextInjector
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const ushort VirtualKeyReturn = 0x0D;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyV = 0x56;
    private const ushort VirtualKeyShift = 0x10;
    private const ushort VirtualKeyMenu = 0x12;
    private const ushort VirtualKeyLeftWindows = 0x5B;
    private const ushort VirtualKeyRightWindows = 0x5C;
    private const uint MapVirtualKeyToScanCode = 0;

    // Remote Desktop and virtual-machine consoles forward the SCAN CODE of a keystroke to the
    // session, not the virtual key. A synthetic key with no scan code therefore arrives as nothing
    // at all on the other side, which is why the paste appeared not to happen there while working
    // locally — and why typing unicode directly, which has no scan code either, was flaky in the
    // first place. Every synthetic key now carries both.
    private const int ChordKeyGapMilliseconds = 15;
    private const uint ClipboardUnicodeText = 13;
    private const uint GlobalMoveable = 0x0002;

    private const uint ClipboardBitmap = 2;
    private const uint ClipboardMetafilePict = 3;
    private const uint ClipboardDib = 8;
    private const uint ClipboardEnhMetafile = 14;
    private const uint ClipboardDibV5 = 17;
    private const uint ImageBitmap = 0;
    private const uint CopyReturnOriginal = 0x0004;

    // Formats whose clipboard handle is neither global memory nor something Bantz can duplicate:
    // palettes and the owner-drawn display formats. They are rare, and dropping them is better
    // than publishing a handle that another app would be wrong to free.
    private static readonly uint[] UnpreservableFormats =
    [
        9,      // CF_PALETTE
        0x0080, // CF_OWNERDISPLAY
        0x0082, // CF_DSPBITMAP
        0x0083, // CF_DSPMETAFILEPICT
        0x008E, // CF_DSPENHMETAFILE
    ];

    // A ceiling on what Bantz will hold in memory to give back afterwards. Clipboards larger than
    // this keep their smaller formats; the oversized one is dropped rather than copied.
    private const long MaximumPreservedBytes = 32L * 1024 * 1024;
    private const int ClipboardOpenAttempts = 10;
    private const int ClipboardRetryMilliseconds = 20;

    // Pasting is asynchronous from here. SendInput only queues the keystrokes: it returns before
    // the target has seen them, and the target then reads the clipboard on its own message loop.
    // A quarter of a second was enough for a local text box and not nearly enough for the case
    // this mode exists for — a Remote Desktop session fetches clipboard data across the wire when
    // the remote application asks for it, which is slower than that, so the transcript had been
    // taken back before it could be read and nothing arrived. It now stays put for as long as a
    // remote fetch plausibly takes.
    private const int PasteHoldMilliseconds = 2_500;
    private const int PasteHoldPollMilliseconds = 50;

    private readonly Func<bool> _useClipboardPaste;

    public WindowsTextInjector()
        : this(null)
    {
    }

    /// <summary>
    /// Creates an injector that reads <paramref name="useClipboardPaste"/> for each transcript, so
    /// the compatibility setting can change while the application runs. Clipboard paste suits
    /// destinations that drop synthesized Unicode keystrokes, such as Remote Desktop sessions and
    /// some virtual-machine consoles.
    /// </summary>
    public WindowsTextInjector(Func<bool>? useClipboardPaste)
    {
        _useClipboardPaste = useClipboardPaste ?? (static () => false);
    }

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

        return _useClipboardPaste()
            ? PasteThroughClipboard(text, pressEnter)
            : SendUnicodeKeystrokes(text, pressEnter);
    }

    private static TextInjectionResult SendUnicodeKeystrokes(string text, bool pressEnter)
    {
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

    /// <summary>
    /// Holds the transcript on the clipboard long enough for the target to read it. Returns false
    /// when somebody has copied something else meanwhile — the one case where handing the previous
    /// contents back would destroy what they just copied. The question asked is whether the
    /// transcript is still there, not whether the clipboard sequence number moved: that number is
    /// bumped by anything that touches the clipboard, Windows' own clipboard history included, so
    /// it reported a takeover on a machine where nobody had copied anything.
    /// </summary>
    private static bool WaitBeforeRestoring(string transcript)
    {
        for (var waited = 0; waited < PasteHoldMilliseconds; waited += PasteHoldPollMilliseconds)
        {
            Thread.Sleep(PasteHoldPollMilliseconds);
            if (ReadClipboardText() is { } current && !string.Equals(current, transcript, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The clipboard's Unicode text, or null when it holds none or cannot be opened.</summary>
    private static string? ReadClipboardText()
    {
        if (!TryOpenClipboard())
        {
            return null;
        }

        try
        {
            var handle = GetClipboardData(ClipboardUnicodeText);
            if (handle == nint.Zero)
            {
                return null;
            }

            var pointer = GlobalLock(handle);
            if (pointer == nint.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                _ = GlobalUnlock(pointer);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// The clipboard half of a paste. <paramref name="sendChord"/> is false only for the
    /// --paste-probe diagnostic, which measures how long the transcript is available without
    /// typing into whatever the person is using.
    /// </summary>
    internal static TextInjectionResult PasteThroughClipboard(string text, bool pressEnter, bool sendChord = true)
    {
        if (!TryOpenClipboard())
        {
            return TextInjectionResult.Failure(
                "Another app is holding the clipboard. Your transcript is still shown in Bantz.");
        }

        // Anything captured has to be handed back or released, including on the failure paths, so
        // every exit from here runs through RestoreClipboard.
        var previousContents = new List<ClipboardEntry>();
        var written = false;
        try
        {
            try
            {
                previousContents = CaptureClipboard();
                if (!EmptyClipboard() ||
                    !WriteClipboardBytes(ClipboardUnicodeText, Encoding.Unicode.GetBytes(text + '\0')))
                {
                    return TextInjectionResult.Failure(
                        "Windows would not accept the transcript onto the clipboard. It is still shown in Bantz.");
                }
            }
            finally
            {
                CloseClipboard();
            }

            written = true;
            if (sendChord)
            {
                SendPasteChord(pressEnter);
            }

            return TextInjectionResult.Success();
        }
        catch (Exception exception) when (exception is Win32Exception or OverflowException)
        {
            return TextInjectionResult.Failure($"Windows could not paste the transcript: {exception.Message}");
        }
        finally
        {
            // Nothing reached the clipboard on the failure paths, so there is nothing to wait for
            // and the previous contents go straight back.
            if (!written || WaitBeforeRestoring(text))
            {
                RestoreClipboard(previousContents);
            }
            else
            {
                foreach (var entry in previousContents)
                {
                    ReleaseHandle(entry);
                }
            }
        }
    }

    private static void SendPasteChord(bool pressEnter)
    {
        // A hold-to-talk shortcut is itself a chord — Ctrl+Shift+Space by default — and the paste
        // is sent the moment it is released. A modifier still physically down turns Ctrl+V into
        // Ctrl+Shift+V, which pastes differently or not at all depending on the application, so
        // anything still held is lifted first.
        ReleaseHeldModifiers();

        // Sent one key at a time rather than as one batch: a remote session has to forward each of
        // these across the wire, and a modifier that arrives in the same instant as the key it
        // modifies is not reliably seen as held.
        SendKey(VirtualKeyControl, keyUp: false);
        SendKey(VirtualKeyV, keyUp: false);
        SendKey(VirtualKeyV, keyUp: true);
        SendKey(VirtualKeyControl, keyUp: true);
        if (pressEnter)
        {
            SendKey(VirtualKeyReturn, keyUp: false);
            SendKey(VirtualKeyReturn, keyUp: true);
        }
    }

    /// <summary>Lifts any modifier the person is still holding, so it cannot join the chord.</summary>
    private static void ReleaseHeldModifiers()
    {
        foreach (var modifier in new[]
                 {
                     VirtualKeyShift, VirtualKeyMenu, VirtualKeyControl,
                     VirtualKeyLeftWindows, VirtualKeyRightWindows,
                 })
        {
            if ((GetAsyncKeyState(modifier) & 0x8000) != 0)
            {
                SendKey(modifier, keyUp: true);
            }
        }
    }

    private static void SendKey(ushort virtualKey, bool keyUp)
    {
        Input[] batch = [VirtualKeyInput(virtualKey, keyUp)];
        if (SendInput(1, batch, Marshal.SizeOf<Input>()) != 1)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        Thread.Sleep(ChordKeyGapMilliseconds);
    }

    /// <summary>
    /// Copies every format currently on the clipboard into memory so the paste can hand them back.
    /// The clipboard must already be open. Formats that cannot be copied are skipped rather than
    /// failing the paste, because delivering the transcript matters more than a perfect round trip.
    /// </summary>
    private static List<ClipboardEntry> CaptureClipboard()
    {
        var formats = new List<uint>();
        uint current = 0;
        while ((current = EnumClipboardFormats(current)) != 0)
        {
            formats.Add(current);
        }

        // Windows derives CF_DIB/CF_DIBV5 from a bitmap and CF_METAFILEPICT from an enhanced
        // metafile. Copying those derived bytes and republishing them loses fidelity, so when the
        // GDI original is present it alone is preserved and Windows re-derives the rest.
        var hasBitmap = formats.Contains(ClipboardBitmap);
        var hasEnhMetafile = formats.Contains(ClipboardEnhMetafile);

        var entries = new List<ClipboardEntry>();
        var preserved = 0L;
        foreach (var format in formats)
        {
            if (Array.IndexOf(UnpreservableFormats, format) >= 0 ||
                (hasBitmap && format is ClipboardDib or ClipboardDibV5) ||
                (hasEnhMetafile && format == ClipboardMetafilePict))
            {
                continue;
            }

            var handle = GetClipboardData(format);
            if (handle == nint.Zero)
            {
                continue;
            }

            if (format is ClipboardBitmap or ClipboardEnhMetafile)
            {
                var copy = format == ClipboardBitmap
                    ? CopyImage(handle, ImageBitmap, 0, 0, CopyReturnOriginal)
                    : CopyEnhMetaFileW(handle, null);
                if (copy != nint.Zero)
                {
                    entries.Add(new ClipboardEntry(format, null, copy));
                }

                continue;
            }

            var size = (long)GlobalSize(handle);
            if (size <= 0 || preserved + size > MaximumPreservedBytes)
            {
                continue;
            }

            var block = GlobalLock(handle);
            if (block == nint.Zero)
            {
                continue;
            }

            try
            {
                var bytes = new byte[size];
                Marshal.Copy(block, bytes, 0, bytes.Length);
                entries.Add(new ClipboardEntry(format, bytes, nint.Zero));
                preserved += size;
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }
        }

        return entries;
    }

    /// <summary>
    /// Puts back every format captured before the paste. An empty capture leaves the transcript on
    /// the clipboard rather than clearing it.
    /// </summary>
    private static void RestoreClipboard(List<ClipboardEntry> previousContents)
    {
        if (previousContents.Count == 0)
        {
            return;
        }

        var transferred = false;
        try
        {
            if (!TryOpenClipboard())
            {
                return;
            }

            try
            {
                if (!EmptyClipboard())
                {
                    return;
                }

                transferred = true;
                foreach (var entry in previousContents)
                {
                    var placed = entry.Data is not null
                        ? WriteClipboardBytes(entry.Format, entry.Data)
                        : SetClipboardData(entry.Format, entry.Handle) != nint.Zero;
                    if (!placed && entry.Data is null)
                    {
                        // Ownership only transfers on success, so this copy is still ours.
                        ReleaseHandle(entry);
                    }
                }
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Win32Exception)
        {
            // The transcript was delivered; leaving it on the clipboard is not worth an error.
        }
        finally
        {
            if (!transferred)
            {
                foreach (var entry in previousContents)
                {
                    ReleaseHandle(entry);
                }
            }
        }
    }

    private static void ReleaseHandle(ClipboardEntry entry)
    {
        if (entry.Handle == nint.Zero)
        {
            return;
        }

        if (entry.Format == ClipboardEnhMetafile)
        {
            _ = DeleteEnhMetaFile(entry.Handle);
        }
        else
        {
            _ = DeleteObject(entry.Handle);
        }
    }

    /// <summary>
    /// One preserved clipboard format: either a copy of its bytes, or a duplicated GDI handle for
    /// the formats whose data does not live in global memory.
    /// </summary>
    private readonly record struct ClipboardEntry(uint Format, byte[]? Data, nint Handle);

    private static bool TryOpenClipboard()
    {
        for (var attempt = 0; attempt < ClipboardOpenAttempts; attempt++)
        {
            if (OpenClipboard(nint.Zero))
            {
                return true;
            }

            Thread.Sleep(ClipboardRetryMilliseconds);
        }

        return false;
    }

    /// <summary>
    /// Places one format's bytes on the already-open, already-emptied clipboard.
    /// </summary>
    private static bool WriteClipboardBytes(uint format, byte[] data)
    {
        var memory = GlobalAlloc(GlobalMoveable, (nuint)data.Length);
        if (memory == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var block = GlobalLock(memory);
        if (block == nint.Zero)
        {
            _ = GlobalFree(memory);
            return false;
        }

        try
        {
            Marshal.Copy(data, 0, block, data.Length);
        }
        finally
        {
            _ = GlobalUnlock(memory);
        }

        if (SetClipboardData(format, memory) == nint.Zero)
        {
            // Ownership only transfers on success, so the block is still ours to release.
            _ = GlobalFree(memory);
            return false;
        }

        return true;
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

    /// <summary>
    /// The scan codes the chord will carry, for the --paste-probe diagnostic. A zero here would
    /// mean a remote session still receives nothing, so it is worth being able to look.
    /// </summary>
    internal static string DescribeChordKeys() => string.Join(", ", new[]
    {
        ("Ctrl", VirtualKeyControl),
        ("V", VirtualKeyV),
        ("Enter", VirtualKeyReturn),
    }.Select(key => $"{key.Item1} vk=0x{key.Item2:X2} scan=0x{MapVirtualKeyW(key.Item2, MapVirtualKeyToScanCode):X2}"));

    private static Input VirtualKeyInput(ushort virtualKey, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                // Both, deliberately: ordinary windows read the virtual key, while a remote session
                // or a virtual-machine console forwards the scan code and sees nothing without it.
                Scan = (ushort)MapVirtualKeyW(virtualKey, MapVirtualKeyToScanCode),
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

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint EnumClipboardFormats(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyW(uint code, uint mapType);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nuint GlobalSize(nint memory);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CopyImage(nint handle, uint type, int width, int height, uint flags);

    [LibraryImport("gdi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CopyEnhMetaFileW(nint metafile, string? file);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteEnhMetaFile(nint metafile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalFree(nint memory);
}
