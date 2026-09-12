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

    /// <summary>
    /// Waits for a thing to become true rather than for a fixed moment: the readers are background
    /// tasks, and a clock-based wait is a test that passes on an idle machine and fails on a busy
    /// one. Which it did, once, before this.
    /// </summary>
    private static async Task WaitFor(Func<bool> condition, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail($"Timed out waiting for {expected}.");
    }

    [Fact]
    public async Task AGamepadButtonIsCaptured()
    {
        InputBinding? captured = null;
        using var monitor = new LinuxHoldInputMonitor(
            () => [], () => true, () => null, [Device(Press(ButtonSouth), Release(ButtonSouth))]);
        monitor.BindingCaptured += binding => captured = binding;

        Assert.True(monitor.BeginCapture());
        monitor.Start();
        await WaitFor(() => captured is not null, "the button to be captured");

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
        monitor.Start();
        await WaitFor(() => captured is not null, "the chord to be captured");

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

        monitor.Start();
        await WaitFor(() => events.Count == 2, "the hold to start and stop");

        Assert.Equal(["pressed", "released"], events);
    }

    /// <summary>
    /// The bound button comes second in the script, so by the time it has fired the unbound one
    /// ahead of it has certainly been processed — which is what makes this a real assertion rather
    /// than a race the test usually wins.
    /// </summary>
    [Fact]
    public async Task AnUnboundButtonDoesNothing()
    {
        var binding = new InputBinding { Device = InputDevice.Gamepad, Code = ButtonSouth };
        var pressed = 0;
        using var monitor = new LinuxHoldInputMonitor(
            () => [binding], () => true, () => null,
            [Device(Press(0x131), Release(0x131), Press(ButtonSouth), Release(ButtonSouth))]);
        monitor.HotkeyPressed += () => pressed++;

        monitor.Start();
        await WaitFor(() => pressed > 0, "the bound button to fire");

        Assert.Equal(1, pressed);
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

        // The toggle is last in the script, so its arrival proves the disabled binding ahead of it
        // was seen and ignored.
        monitor.Start();
        await WaitFor(() => toggled > 0, "the toggle to fire");

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

        monitor.Start();
        await WaitFor(() => events.Count == 2, "the split events to be decoded");

        Assert.Equal(["pressed", "released"], events);
    }

    /// <summary>
    /// A touchscreen's contact events are not buttons and must not become bindings. The gamepad
    /// press after them is what proves the touch was seen and refused rather than still in flight.
    /// </summary>
    [Fact]
    public async Task ATouchContactIsNotCapturedAsABinding()
    {
        InputBinding? captured = null;
        using var monitor = new LinuxHoldInputMonitor(
            () => [], () => true, () => null,
            [Device(Press(0x14a), Release(0x14a), Press(ButtonSouth))]);   // BTN_TOUCH, then A
        monitor.BindingCaptured += binding => captured = binding;

        Assert.True(monitor.BeginCapture());
        monitor.Start();
        await WaitFor(() => captured is not null, "a button to be captured");

        Assert.Equal((uint)ButtonSouth, captured!.Code);
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
        var released = 0;
        monitor.HotkeyPressed += () => pressed++;
        monitor.HotkeyReleased += () => released++;

        // The release is last, so its arrival proves the repeats ahead of it were seen.
        monitor.Start();
        await WaitFor(() => released > 0, "the key to be released");

        Assert.Equal(1, pressed);
    }

    /// <summary>
    /// A trigger is the obvious push-to-talk button on a handheld, and on the pad Steam presents it
    /// is an axis rather than a button — so without this it could not be bound at all, which is
    /// what "I cannot bind any of the gamepad keys" looked like from the outside.
    /// </summary>
    [Fact]
    public async Task ATriggerCanBeCapturedAndHeld()
    {
        const int AbsoluteZ = 0x02;
        InputBinding? captured = null;
        using var monitor = new LinuxHoldInputMonitor(
            () => [], () => true, () => null,
            [Device(Event(EvdevCodes.EventAbsolute, AbsoluteZ, 255))]);
        monitor.BindingCaptured += binding => captured = binding;

        Assert.True(monitor.BeginCapture());
        monitor.Start();
        await WaitFor(() => captured is not null, "the trigger to be captured");

        Assert.Equal(InputDevice.Gamepad, captured!.Device);
        Assert.Equal("Gamepad L2", captured.DisplayName);
    }

    /// <summary>A trigger rests near zero and travels to full; half-way is where it counts as held.</summary>
    [Fact]
    public async Task ATriggerIsHeldPastHalfTravelAndReleasedBelowIt()
    {
        const int AbsoluteZ = 0x02;
        var binding = new InputBinding
        {
            Device = InputDevice.Gamepad,
            Code = (uint)EvdevCodes.AxisBindingCode(AbsoluteZ, 1),
        };
        var events = new List<string>();
        using var monitor = new LinuxHoldInputMonitor(
            () => [binding], () => true, () => null,
            [Device(
                Event(EvdevCodes.EventAbsolute, AbsoluteZ, 40),    // resting, not a press
                Event(EvdevCodes.EventAbsolute, AbsoluteZ, 200),   // held
                Event(EvdevCodes.EventAbsolute, AbsoluteZ, 220),   // still held, not a second press
                Event(EvdevCodes.EventAbsolute, AbsoluteZ, 10))]); // let go
        monitor.HotkeyPressed += () => events.Add("pressed");
        monitor.HotkeyReleased += () => events.Add("released");

        monitor.Start();
        await WaitFor(() => events.Count == 2, "the trigger to be held and released");

        Assert.Equal(["pressed", "released"], events);
    }

    /// <summary>
    /// A hat flicked straight from one side to the other must not leave the first direction held:
    /// the release has to be emitted before the new press.
    /// </summary>
    [Fact]
    public async Task AHatFlickedAcrossReleasesTheDirectionItLeft()
    {
        const int AbsoluteHatX = 0x10;
        var left = new InputBinding
        {
            Device = InputDevice.Gamepad,
            Code = (uint)EvdevCodes.AxisBindingCode(AbsoluteHatX, -1),
        };
        var events = new List<string>();
        using var monitor = new LinuxHoldInputMonitor(
            () => [left], () => true, () => null,
            [Device(
                Event(EvdevCodes.EventAbsolute, AbsoluteHatX, -1),   // left, held
                Event(EvdevCodes.EventAbsolute, AbsoluteHatX, 1))]); // straight to right
        monitor.HotkeyPressed += () => events.Add("pressed");
        monitor.HotkeyReleased += () => events.Add("released");

        monitor.Start();
        await WaitFor(() => events.Count == 2, "left to be pressed and released");

        Assert.Equal(["pressed", "released"], events);
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
