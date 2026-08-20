using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Bantz.Core;

namespace Bantz.Platform.Linux;

public sealed partial class LinuxAudioRecorder : IAudioRecorder, IDisposable
{
    private readonly object _sync = new();
    private Process? _process;
    private string? _recordingPath;
    private bool _disposed;

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

            _recordingPath = Path.Combine(
                Path.GetTempPath(),
                $"bantz-{Environment.ProcessId}-{Guid.NewGuid():N}.wav");
            var start = new ProcessStartInfo
            {
                FileName = "arecord",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-q");
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add("S16_LE");
            start.ArgumentList.Add("-r");
            start.ArgumentList.Add("16000");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("1");
            start.ArgumentList.Add("-t");
            start.ArgumentList.Add("wav");
            start.ArgumentList.Add(_recordingPath);

            try
            {
                _process = Process.Start(start)
                    ?? throw new InvalidOperationException("arecord did not start.");
            }
            catch (Win32Exception exception)
            {
                DeleteRecording();
                throw new InvalidOperationException(
                    "Bantz needs the ALSA 'arecord' command to record on Linux. Install alsa-utils.",
                    exception);
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<Stream> StopAsync(CancellationToken cancellationToken = default)
    {
        Process process;
        string path;
        lock (_sync)
        {
            process = _process ?? throw new InvalidOperationException("The microphone is not recording.");
            path = _recordingPath ?? throw new InvalidOperationException("The recording session is invalid.");
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
            if (!File.Exists(path))
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"arecord did not produce audio. {error}".Trim());
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return new MemoryStream(bytes, writable: false);
        }
        finally
        {
            lock (_sync)
            {
                process.Dispose();
                _process = null;
                DeleteRecording();
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

            _process?.Dispose();
            _process = null;
            DeleteRecording();
            _disposed = true;
        }
    }

    private void DeleteRecording()
    {
        if (_recordingPath is { } path && File.Exists(path))
        {
            File.Delete(path);
        }

        _recordingPath = null;
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SendSignal(int processId, int signal);
}
