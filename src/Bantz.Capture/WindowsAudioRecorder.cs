using Bantz.Speech;
using NAudio;
using NAudio.Wave;

// Bantz.Speech.WaveFormat (what a WAV header declares) and NAudio.Wave.WaveFormat (what NAudio
// records into) are both in scope here. This file means NAudio's.
using WaveFormat = NAudio.Wave.WaveFormat;

namespace Bantz.Capture;

/// <summary>Captures 16 kHz mono PCM from a Windows wave-in device.</summary>
public sealed class WindowsAudioRecorder : IAudioRecorder, IDisposable
{
    private readonly AudioSignalAnalyzer _signalAnalyzer;
    private readonly Func<AudioCaptureOptions> _optionsProvider;
    private readonly object _sync = new();
    private WaveInEvent? _input;
    private CaptureBuffer? _pcm;
    private TaskCompletionSource? _stopped;
    private long _sequence;
    private bool _disposed;

    public WindowsAudioRecorder(
        AudioSignalAnalyzer? signalAnalyzer = null,
        AudioCaptureOptions? options = null)
        : this(signalAnalyzer, () => options ?? AudioCaptureOptions.Default)
    {
    }

    /// <summary>
    /// Creates a recorder that reads its options as each session starts, so a device chosen
    /// while the application runs applies to the next recording.
    /// </summary>
    public WindowsAudioRecorder(AudioSignalAnalyzer? signalAnalyzer, Func<AudioCaptureOptions> optionsProvider)
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
            if (_input is not null)
            {
                throw new InvalidOperationException("The microphone is already recording.");
            }

            var options = _optionsProvider();
            _pcm = new CaptureBuffer(options.RetainBuffer);
            _sequence = 0;
            _signalAnalyzer.Reset();
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _input = new WaveInEvent
            {
                DeviceNumber = ResolveDeviceNumber(options.DeviceId),
                WaveFormat = new WaveFormat(PcmAudio.SpeechSampleRate, 16, PcmAudio.SpeechChannels),
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

    public async ValueTask<PcmAudio> StopAsync(CancellationToken cancellationToken = default)
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

        return new PcmAudio(pcm);
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
        var frameBytes = eventArgs.Buffer.AsMemory(0, eventArgs.BytesRecorded).ToArray();
        lock (_sync)
        {
            _pcm?.Write(frameBytes);
        }

        _signalAnalyzer.AnalyzePcm16(frameBytes);
        FrameCaptured?.Invoke(new AudioFrame(new PcmAudio(frameBytes), Interlocked.Increment(ref _sequence)));
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

    private static int ResolveDeviceNumber(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.Equals(deviceId, "default", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return int.TryParse(deviceId, out var deviceNumber) && deviceNumber >= 0
            ? deviceNumber
            : throw new ArgumentException("Windows device ids must be non-negative wave-in device numbers.", nameof(deviceId));
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
}
