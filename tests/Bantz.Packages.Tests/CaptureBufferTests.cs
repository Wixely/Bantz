using Bantz.Capture;
using Xunit;

namespace Bantz.Packages.Tests;

public class CaptureBufferTests
{
    [Fact]
    public void CaptureRetainsTheRecordingByDefault()
    {
        Assert.True(AudioCaptureOptions.Default.RetainBuffer);
        Assert.True(new AudioCaptureOptions("some-device").RetainBuffer);
    }

    [Fact]
    public void StreamingOptionsKeepTheDeviceChoiceAndDropTheBuffer()
    {
        Assert.False(AudioCaptureOptions.Streaming.RetainBuffer);
        Assert.Null(AudioCaptureOptions.Streaming.DeviceId);

        var chosen = new AudioCaptureOptions("some-device") { RetainBuffer = false };

        Assert.Equal("some-device", chosen.DeviceId);
        Assert.False(chosen.RetainBuffer);
    }

    [Fact]
    public void ARetainingBufferGivesBackEverythingWrittenToIt()
    {
        using var buffer = new CaptureBuffer(retain: true);

        buffer.Write([1, 2, 3, 4]);
        buffer.Destination.Write([5, 6]);

        Assert.True(buffer.Retains);
        Assert.Equal<byte[]>([1, 2, 3, 4, 5, 6], buffer.ToArray());
    }

    /// <summary>
    /// The point of the whole exercise: an always-open microphone must not accrue the audio it has
    /// already handed out as frames. Both routes in have to drop it — the recorder that writes
    /// frames itself, and the one that hands the buffer's stream to a producer.
    /// </summary>
    [Fact]
    public void AStreamingBufferKeepsNothingByEitherRoute()
    {
        using var buffer = new CaptureBuffer(retain: false);

        buffer.Write([1, 2, 3, 4]);
        // An hour of 16 kHz mono s16 is about 115 MB; this is a minute of it.
        buffer.Destination.Write(new byte[PcmSecond * 60]);

        Assert.False(buffer.Retains);
        Assert.Empty(buffer.ToArray());
        Assert.Same(Stream.Null, buffer.Destination);
    }

    [Fact]
    public void ABufferCanBeDisposedWhicheverItIs()
    {
        new CaptureBuffer(retain: true).Dispose();
        new CaptureBuffer(retain: false).Dispose();
    }

    private const int PcmSecond = 16_000 * 2;
}
