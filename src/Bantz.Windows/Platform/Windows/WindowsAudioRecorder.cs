using Bantz.Core;
using NAudio;
using NAudio.Wave;

namespace Bantz.Platform.Windows;

public sealed class WindowsAudioRecorder : IAudioRecorder, IDisposable
{
    private readonly object _sync = new();
    private WaveInEvent? _input;
    private MemoryStream? _pcm;
    private TaskCompletionSource? _stopped;
    private bool _disposed;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_input is not null)
            {
                throw new InvalidOperationException("The microphone is already recording.");
            }

            _pcm = new MemoryStream();
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _input = new WaveInEvent
            {
                DeviceNumber = 0,
                WaveFormat = new WaveFormat(16_000, 16, 1),
                BufferMilliseconds = 50,
            };
            _input.DataAvailable += OnDataAvailable;
            _input.RecordingStopped += OnRecordingStopped;
            try
            {
                _input.StartRecording();
            }
            catch
            {
                CleanupInput();
                throw;
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<Stream> StopAsync(CancellationToken cancellationToken = default)
    {
        WaveInEvent input;
        Task stopped;
        lock (_sync)
        {
            input = _input ?? throw new InvalidOperationException("The microphone is not recording.");
            stopped = _stopped?.Task ?? throw new InvalidOperationException("The recording session is invalid.");
            input.StopRecording();
        }

        byte[] pcm;
        try
        {
            await stopped.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                pcm = _pcm?.ToArray() ?? [];
                CleanupInput();
            }
        }

        return CreateWaveStream(pcm, 16_000, 1, 16);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            if (_input is not null)
            {
                try
                {
                    _input.StopRecording();
                }
                catch (MmException)
                {
                    // The device may already have vanished; cleanup still needs to run.
                }
            }

            CleanupInput();
            _disposed = true;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        lock (_sync)
        {
            _pcm?.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        if (eventArgs.Exception is null)
        {
            _stopped?.TrySetResult();
        }
        else
        {
            _stopped?.TrySetException(eventArgs.Exception);
        }
    }

    private void CleanupInput()
    {
        if (_input is not null)
        {
            _input.DataAvailable -= OnDataAvailable;
            _input.RecordingStopped -= OnRecordingStopped;
            _input.Dispose();
            _input = null;
        }

        _pcm?.Dispose();
        _pcm = null;
        _stopped = null;
    }

    private static MemoryStream CreateWaveStream(byte[] pcm, int sampleRate, short channels, short bitsPerSample)
    {
        var stream = new MemoryStream(capacity: checked(pcm.Length + 44));
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            var blockAlign = checked((short)(channels * bitsPerSample / 8));
            var bytesPerSecond = checked(sampleRate * blockAlign);
            writer.Write("RIFF"u8);
            writer.Write(checked(36 + pcm.Length));
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(bytesPerSecond);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);
            writer.Write("data"u8);
            writer.Write(pcm.Length);
            writer.Write(pcm);
        }

        stream.Position = 0;
        return stream;
    }
}
