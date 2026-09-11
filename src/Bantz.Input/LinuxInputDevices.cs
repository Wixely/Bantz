using System.Globalization;
using System.Text;

namespace Bantz.Input;

/// <summary>One device as the kernel describes it in <c>/proc/bus/input/devices</c>.</summary>
/// <param name="Name">The device's own name, e.g. "Valve Software Steam Deck Controller".</param>
/// <param name="Handlers">The handlers the kernel attached, e.g. <c>event3</c>, <c>js0</c>.</param>
/// <param name="Keys">Key and button codes the device can report.</param>
/// <param name="AbsoluteAxes">Absolute axis codes the device can report.</param>
public sealed record LinuxInputDevice(
    string Name,
    IReadOnlyList<string> Handlers,
    IReadOnlySet<int> Keys,
    IReadOnlySet<int> AbsoluteAxes)
{
    // Kernel codes, from linux/input-event-codes.h.
    private const int ButtonJoystickFirst = 0x120;
    private const int ButtonGamepadLast = 0x13f;
    private const int ButtonTouch = 0x14a;
    private const int AbsoluteMultiTouchX = 0x35;
    private const int KeyA = 30;
    private const int ButtonLeft = 0x110;

    /// <summary>The evdev node to read this device from, or null when it has no event handler.</summary>
    public string? EventNode =>
        Handlers.FirstOrDefault(handler => handler.StartsWith("event", StringComparison.Ordinal)) is { } handler
            ? "/dev/input/" + handler
            : null;

    /// <summary>Reports gamepad or joystick buttons, which is what a binding would capture.</summary>
    public bool HasGamepadButtons =>
        Keys.Any(code => code is >= ButtonJoystickFirst and <= ButtonGamepadLast);

    /// <summary>Reports touches — a touchscreen or a touchpad.</summary>
    public bool HasTouch => Keys.Contains(ButtonTouch) || AbsoluteAxes.Contains(AbsoluteMultiTouchX);

    /// <summary>Reports ordinary typing keys.</summary>
    public bool HasKeyboardKeys => Keys.Contains(KeyA);

    /// <summary>Reports mouse buttons.</summary>
    public bool HasMouseButtons => Keys.Contains(ButtonLeft);
}

/// <summary>
/// What input hardware the kernel says is present, read from <c>/proc/bus/input/devices</c>.
///
/// <para>This is a diagnostic, not the input path. Bantz has no global input on Linux at all — see
/// <see cref="GlobalInputCapabilities"/> — and building one means reading evdev nodes directly,
/// which needs to know which nodes exist and whether this user may open them. That question is
/// answered differently on a Steam Deck than on a desktop, and cannot be guessed from here.</para>
/// </summary>
public static class LinuxInputDevices
{
    private const string DevicesPath = "/proc/bus/input/devices";

    /// <summary>Every device the kernel lists, or an empty list off Linux.</summary>
    public static IReadOnlyList<LinuxInputDevice> List()
    {
        try
        {
            return File.Exists(DevicesPath) ? Parse(File.ReadAllText(DevicesPath)) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Parses the kernel's listing. Public so the parsing can be tested off Linux.</summary>
    public static IReadOnlyList<LinuxInputDevice> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var devices = new List<LinuxInputDevice>();
        var name = "";
        var handlers = new List<string>();
        var keys = new HashSet<int>();
        var axes = new HashSet<int>();

        void Flush()
        {
            if (name.Length > 0 || handlers.Count > 0)
            {
                devices.Add(new LinuxInputDevice(name, [.. handlers], keys, axes));
            }

            name = "";
            handlers = [];
            keys = [];
            axes = [];
        }

        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            if (line.StartsWith("N: Name=", StringComparison.Ordinal))
            {
                name = line["N: Name=".Length..].Trim('"');
            }
            else if (line.StartsWith("H: Handlers=", StringComparison.Ordinal))
            {
                handlers.AddRange(line["H: Handlers=".Length..]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            else if (line.StartsWith("B: KEY=", StringComparison.Ordinal))
            {
                keys = SetBits(line["B: KEY=".Length..]);
            }
            else if (line.StartsWith("B: ABS=", StringComparison.Ordinal))
            {
                axes = SetBits(line["B: ABS=".Length..]);
            }
        }

        Flush();
        return devices;
    }

    /// <summary>
    /// The codes a kernel capability bitmap has set. The words are 64-bit hex, most significant
    /// first, so the last word holds codes 0-63.
    /// </summary>
    public static HashSet<int> SetBits(string bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var codes = new HashSet<int>();
        var words = bitmap.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < words.Length; index++)
        {
            if (!ulong.TryParse(words[index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var word))
            {
                continue;
            }

            var offset = (words.Length - 1 - index) * 64;
            for (var bit = 0; bit < 64; bit++)
            {
                if ((word & (1UL << bit)) != 0)
                {
                    codes.Add(offset + bit);
                }
            }
        }

        return codes;
    }

    /// <summary>
    /// A report for a machine Bantz cannot be attached to with a debugger — what the kernel offers,
    /// and whether this user may read it, which is what decides if an evdev input path can work
    /// without extra permissions.
    /// </summary>
    public static string Describe()
    {
        var report = new StringBuilder();
        report.Append("Platform: ").AppendLine(Environment.OSVersion.VersionString);
        report.Append("Global bindings implemented here: ")
              .AppendLine(GlobalInputCapabilities.Current.SupportsGlobalBindings ? "yes" : "no");

        var devices = List();
        if (devices.Count == 0)
        {
            report.AppendLine($"No devices listed. Is {DevicesPath} present?");
            return report.ToString();
        }

        report.Append(devices.Count).AppendLine(" devices:");
        foreach (var device in devices)
        {
            var kinds = new List<string>();
            if (device.HasGamepadButtons) kinds.Add("gamepad");
            if (device.HasTouch) kinds.Add("touch");
            if (device.HasKeyboardKeys) kinds.Add("keyboard");
            if (device.HasMouseButtons) kinds.Add("mouse");

            report.Append("  ").Append(device.Name.Length > 0 ? device.Name : "(unnamed)")
                  .Append(" [").Append(kinds.Count > 0 ? string.Join(", ", kinds) : "other").AppendLine("]");
            report.Append("    node: ").Append(device.EventNode ?? "(none)")
                  .Append("  readable: ").AppendLine(CanRead(device.EventNode));
        }

        return report.ToString();
    }

    private static string CanRead(string? node)
    {
        if (node is null)
        {
            return "n/a";
        }

        try
        {
            using var stream = File.OpenRead(node);
            return "yes";
        }
        catch (UnauthorizedAccessException)
        {
            return "no (permission denied — this user is probably not in the 'input' group)";
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            return $"no ({exception.GetType().Name})";
        }
    }
}
