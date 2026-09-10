using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Bantz.Capture;

/// <summary>A microphone that capture can record from.</summary>
/// <param name="Id">The value to pass as <see cref="AudioCaptureOptions.DeviceId"/>.</param>
/// <param name="Name">The name to show a person choosing a microphone.</param>
public sealed record AudioCaptureDevice(string Id, string Name);

/// <summary>Lists the microphones the current platform can record from.</summary>
public static class AudioCaptureDevices
{
    private const int LinuxProbeMilliseconds = 2_000;

    /// <summary>The device id that always means "whatever the operating system prefers".</summary>
    public static string DefaultId => "default";

    /// <summary>The entry that follows the operating system's own choice of microphone.</summary>
    public static AudioCaptureDevice Default { get; } = new(DefaultId, "System default");

    /// <summary>
    /// Lists the available microphones, starting with <see cref="Default"/>. Enumeration asks the
    /// operating system each time, so the result reflects devices plugged in since the last call.
    /// Platforms without a usable enumeration path report the default device alone.
    /// </summary>
    public static IReadOnlyList<AudioCaptureDevice> List()
    {
        try
        {
            return OperatingSystem.IsWindows() ? ListWindowsDevices() : ListAlsaDevices();
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or IOException or NAudio.MmException)
        {
            return [Default];
        }
    }

    /// <summary>
    /// Describes what each layer reports, for working out why a microphone is named or listed the
    /// way it is. Wave-in names arrive cut to 31 characters, so the report shows both the raw name
    /// and the endpoint name Bantz matched it to.
    /// </summary>
    public static string Describe()
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Platform: {(OperatingSystem.IsWindows() ? "Windows" : "non-Windows")}");
        if (OperatingSystem.IsWindows())
        {
            try
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"Wave-in devices: {WaveInEvent.DeviceCount}");
                for (var number = 0; number < WaveInEvent.DeviceCount; number++)
                {
                    var raw = WaveInEvent.GetCapabilities(number).ProductName ?? string.Empty;
                    report.AppendLine(CultureInfo.InvariantCulture, $"  [{number}] \"{raw}\" ({raw.Length} chars)");
                }
            }
            catch (Exception exception) when (exception is Win32Exception or NAudio.MmException)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"Wave-in enumeration failed: {exception.GetType().Name}: {exception.Message}");
            }

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                report.AppendLine(CultureInfo.InvariantCulture, $"Core Audio capture endpoints: {endpoints.Count}");
                foreach (var endpoint in endpoints)
                {
                    using (endpoint)
                    {
                        report.AppendLine(CultureInfo.InvariantCulture, $"  \"{endpoint.FriendlyName}\" ({endpoint.FriendlyName.Length} chars)");
                    }
                }
            }
            catch (Exception exception) when (
                exception is COMException or InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"Core Audio unavailable: {exception.GetType().Name}: {exception.Message}");
            }
        }

        report.AppendLine("Devices Bantz will show:");
        foreach (var device in List())
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  id={device.Id} name=\"{device.Name}\" ({device.Name.Length} chars)");
        }

        return report.ToString();
    }

    private static AudioCaptureDevice[] ListWindowsDevices()
    {
        // Wave-in reports names through a 32-character struct field, so anything longer arrives
        // cut short ("Microphone (SteelSeries Arctis"). Core Audio knows the full name, so the
        // wave-in number stays the id and the endpoint supplies the label.
        var endpointNames = CaptureEndpointNames();
        var claimed = new bool[endpointNames.Length];
        var devices = new List<AudioCaptureDevice> { Default };
        for (var number = 0; number < WaveInEvent.DeviceCount; number++)
        {
            var name = WaveInEvent.GetCapabilities(number).ProductName?.Trim() ?? string.Empty;
            devices.Add(new AudioCaptureDevice(
                number.ToString(CultureInfo.InvariantCulture),
                ExpandDeviceName(name, number, endpointNames, claimed)));
        }

        return [.. devices];
    }

    /// <summary>
    /// Matches a truncated wave-in name to the full endpoint name that begins with it. Identical
    /// microphones truncate identically, so each endpoint name is claimed at most once.
    /// </summary>
    internal static string ExpandDeviceName(string waveInName, int number, string[] endpointNames, bool[] claimed)
    {
        if (waveInName.Length == 0)
        {
            return $"Microphone {number}";
        }

        for (var index = 0; index < endpointNames.Length; index++)
        {
            if (!claimed[index] &&
                endpointNames[index].StartsWith(waveInName, StringComparison.OrdinalIgnoreCase))
            {
                claimed[index] = true;
                return endpointNames[index];
            }
        }

        return waveInName;
    }

    private static string[] CaptureEndpointNames()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var names = new List<string>();
            foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (endpoint)
                {
                    var name = endpoint.FriendlyName;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name.Trim());
                    }
                }
            }

            return [.. names];
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
        {
            // Without Core Audio the truncated wave-in names still identify each microphone.
            return [];
        }
    }

    private static AudioCaptureDevice[] ListAlsaDevices()
    {
        var start = new ProcessStartInfo
        {
            FileName = "arecord",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-L");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("arecord did not start.");
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(LinuxProbeMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            return [Default];
        }

        return ParseAlsaDevices(output);
    }

    /// <summary>
    /// Reads the two-line-per-entry listing that <c>arecord -L</c> writes: an unindented ALSA
    /// device name followed by indented description lines.
    /// </summary>
    internal static AudioCaptureDevice[] ParseAlsaDevices(string listing)
    {
        var devices = new List<AudioCaptureDevice> { Default };
        var seen = new HashSet<string>(StringComparer.Ordinal) { DefaultId };
        string? pendingId = null;
        foreach (var line in listing.Split('\n'))
        {
            var text = line.TrimEnd('\r', ' ', '\t');
            if (text.Length == 0)
            {
                continue;
            }

            if (char.IsWhiteSpace(line[0]))
            {
                if (pendingId is not null)
                {
                    devices.Add(new AudioCaptureDevice(pendingId, text.Trim()));
                    pendingId = null;
                }

                continue;
            }

            if (pendingId is not null)
            {
                devices.Add(new AudioCaptureDevice(pendingId, pendingId));
                pendingId = null;
            }

            if (!string.Equals(text, "null", StringComparison.Ordinal) && seen.Add(text))
            {
                pendingId = text;
            }
        }

        if (pendingId is not null)
        {
            devices.Add(new AudioCaptureDevice(pendingId, pendingId));
        }

        return [.. devices];
    }
}
