using System.Buffers.Binary;
using Bantz.Input;
using Xunit;

namespace Bantz.Core.Tests;

/// <summary>
/// The monitor reads devices as streams, so the whole decoding path — event framing, key codes,
/// modifiers, capture and hold — runs here against synthetic events, on a machine with no gamepad
/// and no evdev at all. Only opening the real device nodes needs Linux.
/// </summary>
public class LinuxHoldInputMonitorTests
{
    private const int ButtonSouth = 0x130;   // the Steam Deck's A
    private const int KeySpace = 57;
    private const int KeyLeftShift = 42;

    /// <summary>One evdev event: 16 bytes of timeval, then type, code and value.</summary>
    private static byte[] Event(ushort type, ushort code, int value)
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), type);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), code);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), value);
        return bytes;
    }

    private static byte[] Press(int code) => Event(EvdevCodes.EventKey, (ushort)code, 1);
    private static byte[] Release(int code) => Event(EvdevCodes.EventKey, (ushort)code, 0);

    /// <summary>A device that hands over a prepared script of events and then ends.</summary>
    private static MemoryStream Device(params byte[][] events) =>
        new MemoryStream(events.SelectMany(bytes => bytes).ToArray());

    private static async Task SettleAsync() => await Task.Delay(150);

    [Fact]
    public async Task AGamepadButtonIsCaptured()
    {
        InputBinding? captured = null;
        using var monitor = new LinuxHoldInputMonitor(
            () => [], () => true, () => null, [Device(Press(ButtonSouth), Release(ButtonSouth))]);
        monitor.BindingCaptured += binding => captured = binding;

        Assert.True(monitor.BeginCapture());
        await SettleAsync();

        Assert.NotNull(captured);
        Assert.Equal(InputDevice.Gamepad, captured.Device);
        Assert.Equal((uint)ButtonSouth, captured.Code);
        Assert.Equal("Gamepad A", captured.DisplayName);
    }

    /// <summary>
    /// The reason this was reported as a gamepad problem is that nothing bound at all. A keyboard
    /// press has to be captured with whatever modifiers are held at the time.
    /// </summary>
    [Fact]
    public async Task AKeyboardChordIsCapturedWithItsModifiers()
    {
        InputBinding? captured = null;
        using var monitor = new LinuxHoldInputMonitor(
            () => [], () => true, () => null,
            [Device(Press(KeyLeftShift), Press(KeySpace), Release(KeySpace), Release(KeyLeftShift))]);
        monitor.BindingCaptured += binding => captured = binding;

        Assert.True(monitor.BeginCapture());
        await SettleAsync();

        Assert.NotNull(captured);
        Assert.Equal(InputDevice.Keyboard, captured.Device);
        Assert.Equal(KeyboardModifiers.Shift, captured.Modifiers);
        Assert.Equal("Shift + Space", captured.DisplayName);
    }

    [Fact]
    public async Task HoldingABoundGamepadButtonStartsAndStopsDictation()
    {
        var binding = new InputBinding { Device = InputDevice.Gamepad, Code = ButtonSouth };
        var events = new List<string>();
        using var monitor = new LinuxHoldInputMonitor(
            () => [binding], () => true, () => null, [Device(Press(ButtonSouth), Release(ButtonSouth))]);
        monitor.HotkeyPressed += () => events.Add("pressed");
        monitor.HotkeyReleased += () => events.Add("released");

        await SettleAsync();

        Assert.Equal(["pressed", "released"], events);
    }

    [Fact]
    public async Task AnUnboundButtonDoesNothing()
    {
        var binding = new InputBinding { Device = InputDevice.Gamepad, Code = ButtonSouth };
        var events = new List<string>();
        using var monitor = new LinuxHoldInputMonitor(
            () => [binding], () => true, () => null, [Device(Press(0x131), Release(0x131))]);
        monitor.HotkeyPressed += () => events.Add("pressed");

        await SettleAsync();

        Assert.Empty(events);
    }

    /// <summary>Turning shortcuts off has to stop them firing, without stopping the toggle itself.</summary>
    [Fact]
    public async Task DisabledShortcutsDoNotFireButTheToggleStillDoes()
    {
        var binding = new InputBinding { Device = InputDevice.Gamepad, Code = ButtonSouth };
        var toggle = new InputBinding { Device = InputDevice.Gamepad, Code = 0x13b };
        var pressed = 0;
        var toggled = 0;
        using var monitor = new LinuxHoldInputMonitor(
            () => [binding], () => false, () => toggle,
            [Device(Press(ButtonSouth), Release(ButtonSouth), Press(0x13b), Release(0x13b))]);
        monitor.HotkeyPressed += () => pressed++;
        monitor.ShortcutTogglePressed += () => toggled++;

        await SettleAsync();

        Assert.Equal(0, pressed);
        Assert.Equal(1, toggled);
    }

    /// <summary>
    /// A read can end mid-event, and a device that reports quickly will deliver several at once.
    /// Decoding has to carry the remainder rather than lose or misalign it.
    /// </summary>
    [Fact]
    public async Task EventsSplitAcrossReadsAreStillDecoded()
    {
        var script = Press(ButtonSouth).Concat(Release(ButtonSouth)).ToArray();
        var events = new List<string>();
        var binding = new InputBinding { Device = InputDevice.Gamepad, Code = ButtonSouth };
        using var monitor = new LinuxHoldInputMonitor(
            () => [binding], () => true, () => null, [new DribblingStream(script, chunk: 7)]);
        monitor.HotkeyPressed += () => events.Add("pressed");
        monitor.HotkeyReleased += () => events.Add("released");

        await SettleAsync();

        Assert.Equal(["pressed", "released"], events);
    }

    /// <summary>A touchscreen's contact events are not buttons and must not become bindings.</summary>
    [Fact]
    public async Task ATouchContactIsNotCapturedAsABinding()
    {
        InputBinding? captured = null;
        using var monitor = new LinuxHoldInputMonitor(
            () => [], () => true, () => null, [Device(Press(0x14a), Release(0x14a))]);   // BTN_TOUCH
        monitor.BindingCaptured += binding => captured = binding;

        Assert.True(monitor.BeginCapture());
        await SettleAsync();

        Assert.Null(captured);
    }

    /// <summary>Key repeats arrive as value 2 and are neither a press nor a release.</summary>
    [Fact]
    public async Task KeyRepeatsDoNotRetriggerAHold()
    {
        var binding = new InputBinding { Device = InputDevice.Keyboard, Code = KeySpace };
        var pressed = 0;
        using var monitor = new LinuxHoldInputMonitor(
            () => [binding], () => true, () => null,
            [Device(Press(KeySpace), Event(EvdevCodes.EventKey, KeySpace, 2), Event(EvdevCodes.EventKey, KeySpace, 2), Release(KeySpace))]);
        monitor.HotkeyPressed += () => pressed++;

        await SettleAsync();

        Assert.Equal(1, pressed);
    }

    /// <summary>Hands its content over a few bytes at a time, as a device under load would.</summary>
    private sealed class DribblingStream(byte[] content, int chunk) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var remaining = content.Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var take = Math.Min(Math.Min(chunk, buffer.Length), remaining);
            content.AsSpan(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
