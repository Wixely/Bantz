using Bantz.Speech;

namespace Bantz.Capture;

/// <summary>Options for a microphone capture session.</summary>
public sealed record AudioCaptureOptions(string? DeviceId = null)
{
    /// <summary>The platform default capture device.</summary>
    public static AudioCaptureOptions Default { get; } = new();
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

    /// <summary>Stops recording and returns the complete normalized buffer.</summary>
    ValueTask<PcmAudio> StopAsync(CancellationToken cancellationToken = default);
}
