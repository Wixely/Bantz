using Bantz.Speech;

namespace Bantz.Capture;

/// <summary>Options for a microphone capture session.</summary>
public sealed record AudioCaptureOptions(string? DeviceId = null)
{
    /// <summary>The platform default capture device.</summary>
    public static AudioCaptureOptions Default { get; } = new();

    /// <summary>
    /// Whether the session keeps the audio it emits, so that stopping can return the whole
    /// recording. True suits hold-to-talk, where the press lasts seconds and the buffer is the
    /// product. False suits a microphone that stays open — a phone or a desktop acting as a room
    /// microphone, consuming <see cref="IAudioRecorder.FrameCaptured"/> and never reading the
    /// buffer — where retaining it costs about 115 MB an hour that nothing wants.
    /// </summary>
    public bool RetainBuffer { get; init; } = true;

    /// <summary>The platform default device, with nothing retained.</summary>
    public static AudioCaptureOptions Streaming { get; } = new() { RetainBuffer = false };
}

/// <summary>A chunk of normalized PCM emitted during capture.</summary>
public sealed record AudioFrame(PcmAudio Audio, long Sequence);

/// <summary>Records normalized speech audio until stopped.</summary>
public interface IAudioRecorder
{
    /// <summary>Raised for each normalized frame while recording.</summary>
    event Action<AudioFrame>? FrameCaptured
    {
        add { }
        remove { }
    }

    /// <summary>Starts recording from the selected device.</summary>
    ValueTask StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops recording and returns the complete normalized buffer, or an empty one when the
    /// session was started with <see cref="AudioCaptureOptions.RetainBuffer"/> false.
    ///
    /// <para>This does not return until no further <see cref="FrameCaptured"/> will be raised, so a
    /// caller can hand off the last of the audio without racing the backend.</para>
    /// </summary>
    ValueTask<PcmAudio> StopAsync(CancellationToken cancellationToken = default);
}
