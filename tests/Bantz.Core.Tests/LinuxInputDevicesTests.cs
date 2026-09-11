using Bantz.Input;
using Xunit;

namespace Bantz.Core.Tests;

/// <summary>
/// The parsing runs off Linux so it can be tested here; only the reading of
/// <c>/proc/bus/input/devices</c> itself needs the machine.
/// </summary>
public class LinuxInputDevicesTests
{
    // Shaped like a Steam Deck's listing: the built-in controller, the touchscreen, and a keyboard
    // that is not either of them.
    private const string DeckListing = """
        I: Bus=0003 Vendor=28de Product=1205 Version=0111
        N: Name="Valve Software Steam Deck Controller"
        P: Phys=usb-0000:04:00.3-3/input1
        S: Sysfs=/devices/pci0000:00/0000:00:08.1/0000:04:00.3/usb3/3-3/3-3:1.1/0003:28DE:1205.0002/input/input3
        U: Uniq=
        H: Handlers=event3 js0
        B: PROP=0
        B: EV=100003
        B: KEY=7fdb000000000000 0 0 0 0

        I: Bus=0018 Vendor=27c6 Product=0104 Version=0100
        N: Name="FTS3528:00 2808:1015"
        P: Phys=
        S: Sysfs=/devices/platform/AMDI0010:03/i2c-1/i2c-FTS3528:00/0018:27C6:0104.0003/input/input5
        U: Uniq=
        H: Handlers=event5
        B: PROP=2
        B: EV=b
        B: KEY=400 0 0 0 0 0
        B: ABS=2608000 20000000000003

        I: Bus=0011 Vendor=0001 Product=0001 Version=ab41
        N: Name="AT Translated Set 2 keyboard"
        P: Phys=isa0060/serio0
        S: Sysfs=/devices/platform/i8042/serio0/input/input0
        U: Uniq=
        H: Handlers=sysrq kbd event0 leds
        B: PROP=0
        B: EV=120013
        B: KEY=402000000 3803078f800d001 feffffdfffefffff fffffffffffffffe

        """;

    [Fact]
    public void TheControllerIsRecognisedAsAGamepadWithANodeToReadItFrom()
    {
        var devices = LinuxInputDevices.Parse(DeckListing);

        var controller = Assert.Single(devices, device => device.Name.Contains("Steam Deck Controller", StringComparison.Ordinal));
        Assert.True(controller.HasGamepadButtons);
        Assert.Equal("/dev/input/event3", controller.EventNode);
        Assert.False(controller.HasKeyboardKeys);
    }

    [Fact]
    public void TheTouchscreenIsRecognisedByItsMultiTouchAxes()
    {
        var devices = LinuxInputDevices.Parse(DeckListing);

        var touchscreen = Assert.Single(devices, device => device.HasTouch);

        Assert.Equal("/dev/input/event5", touchscreen.EventNode);
        Assert.False(touchscreen.HasGamepadButtons);
    }

    [Fact]
    public void AKeyboardIsNeitherOfThose()
    {
        var devices = LinuxInputDevices.Parse(DeckListing);

        var keyboard = Assert.Single(devices, device => device.HasKeyboardKeys);

        Assert.Equal("/dev/input/event0", keyboard.EventNode);
        Assert.False(keyboard.HasGamepadButtons);
        Assert.False(keyboard.HasTouch);
        Assert.Equal(3, devices.Count);
    }

    /// <summary>
    /// The words are most significant first, so the last one carries codes 0-63. Getting this
    /// backwards would report every device as having the wrong buttons.
    /// </summary>
    [Fact]
    public void CapabilityBitsAreReadFromTheLeastSignificantWordLast()
    {
        // Two words: bit 0 of the last is code 0, bit 0 of the first is code 64.
        var codes = LinuxInputDevices.SetBits("1 8000000000000003");

        Assert.Contains(64, codes);
        Assert.Contains(0, codes);
        Assert.Contains(1, codes);
        Assert.Contains(63, codes);
        Assert.Equal(4, codes.Count);
    }

    [Fact]
    public void ADeviceWithNoEventHandlerCannotBeRead()
    {
        var devices = LinuxInputDevices.Parse("""
            N: Name="Only a joystick node"
            H: Handlers=js0
            B: KEY=7fdb000000000000 0 0 0 0
            """);

        var device = Assert.Single(devices);
        Assert.Null(device.EventNode);
        Assert.True(device.HasGamepadButtons);
    }
}
