using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Bantz.Speech;

namespace Bantz.Capture;

/// <summary>Captures 16 kHz mono PCM through ALSA's arecord command.</summary>
public sealed partial class LinuxAudioRecorder : IAudioRecorder, IDisposable
{
    private readonly AudioSignalAnalyzer _signalAnalyzer;
    private readonly Func<AudioCaptureOptions> _optionsProvider;
    private readonly object _sync = new();
    private Process? _process;
    private CaptureBuffer? _pcm;
    private Task? _captureTask;
    private long _sequence;
    private bool _disposed;

    public LinuxAudioRecorder(
        AudioSignalAnalyzer? signalAnalyzer = null,
        AudioCaptureOptions? options = null)
        : this(signalAnalyzer, () => options ?? AudioCaptureOptions.Default)
    {
    }

    /// <summary>
    /// Creates a recorder that reads its options as each session starts, so a device chosen
    /// while the application runs applies to the next recording.
    /// </summary>
    public LinuxAudioRecorder(AudioSignalAnalyzer? signalAnalyzer, Func<AudioCaptureOptions> optionsProvider)
    {
        ArgumentNullException.ThrowIfNull(optionsProvider);
        _signalAnalyzer = signalAnalyzer ?? new AudioSignalAnalyzer();
        _optionsProvider = optionsProvider;
    }

    public event Action<AudioFrame>? FrameCaptured;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_process is not null)
            {
                throw new InvalidOperationException("The microphone is already recording.");
            }

            var start = new ProcessStartInfo
            {
                FileName = "arecord",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            start.ArgumentList.Add("-q");
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add("S16_LE");
            start.ArgumentList.Add("-r");
            start.ArgumentList.Add(PcmAudio.SpeechSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(PcmAudio.SpeechChannels.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-t");
            start.ArgumentList.Add("raw");
            // Read once, so the device and the retention decision come from the same snapshot.
            var options = _optionsProvider();
            var deviceId = options.DeviceId;
            if (!string.IsNullOrWhiteSpace(deviceId) &&
                !string.Equals(deviceId, AudioCaptureDevices.DefaultId, StringComparison.OrdinalIgnoreCase))
            {
                start.ArgumentList.Add("-D");
                start.ArgumentList.Add(deviceId);
            }

            try
            {
                _process = Process.Start(start) ?? throw new InvalidOperationException("arecord did not start.");
                _pcm = new CaptureBuffer(options.RetainBuffer);
                _sequence = 0;
                _signalAnalyzer.Reset();
                _captureTask = CaptureAudioAsync(_process.StandardOutput.BaseStream, _pcm.Destination);
            }
            catch (Win32Exception exception)
            {
                CleanupCapture();
                throw new InvalidOperationException(
                    "Linux capture needs the ALSA 'arecord' command. Install alsa-utils.",
                    exception);
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<PcmAudio> StopAsync(CancellationToken cancellationToken = default)
    {
        Process process;
        Task captureTask;
        lock (_sync)
        {
            process = _process ?? throw new InvalidOperationException("The microphone is not recording.");
            captureTask = _captureTask ?? throw new InvalidOperationException("The recording session is invalid.");
        }

        try
        {
            if (!process.HasExited && SendSignal(process.Id, 2) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not stop arecord cleanly.");
            }

            using var stopTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stopTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(stopTimeout.Token).ConfigureAwait(false);
            await captureTask.WaitAsync(stopTimeout.Token).ConfigureAwait(false);
            var retained = _pcm?.Retains ?? false;
            var bytes = _pcm?.ToArray() ?? [];
            // A streaming session keeps nothing on purpose, so an empty buffer says nothing about
            // whether the microphone worked.
            if (retained && bytes.Length == 0)
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"arecord did not produce audio. {error}".Trim());
            }

            return new PcmAudio(bytes.Length % sizeof(short) == 0 ? bytes : bytes[..^1]);
        }
        finally
        {
            lock (_sync)
            {
                CleanupCapture();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            if (_process is { HasExited: false } process)
            {
                _ = SendSignal(process.Id, 2);
                if (!process.WaitForExit(1000))
                {
                    process.Kill(entireProcessTree: true);
                }
            }

            try
            {
                _captureTask?.GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // Shutdown still owns and releases the process and capture buffer.
            }

            CleanupCapture();
            _disposed = true;
        }
    }

    private async Task CaptureAudioAsync(Stream source, Stream destination)
    {
        var buffer = new byte[1601];
        var carry = 0;
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer.AsMemory(carry, 1600)).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return;
            }

            await destination.WriteAsync(buffer.AsMemory(carry, bytesRead)).ConfigureAwait(false);
            var available = carry + bytesRead;
            var analysisBytes = available - (available % sizeof(short));
            if (analysisBytes != 0)
            {
                var frameBytes = buffer.AsMemory(0, analysisBytes).ToArray();
                _signalAnalyzer.AnalyzePcm16(frameBytes);
                FrameCaptured?.Invoke(new AudioFrame(new PcmAudio(frameBytes), Interlocked.Increment(ref _sequence)));
            }
            carry = available - analysisBytes;
            if (carry != 0)
            {
                buffer[0] = buffer[analysisBytes];
            }
        }
    }

    private void CleanupCapture()
    {
        _process?.Dispose();
        _process = null;
        _pcm?.Dispose();
        _pcm = null;
        _captureTask = null;
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SendSignal(int processId, int signal);
}
