using Bantz.Ui;
using SkiaSharp;
using Xunit;

namespace Bantz.Core.Tests;

public sealed class RecordingStateIconTests
{
    [Fact]
    public void RecordingFramesAddAnAnimatedCoralEqualizerBadge()
    {
        using var source = new SKBitmap(64, 64);
        source.Erase(new SKColor(0x68, 0xe0, 0xb1));
        using var image = SKImage.FromBitmap(source);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        var frames = RecordingStateIcon.CreateFrames(encoded.ToArray());

        Assert.Equal(RecordingStateIcon.FrameCount, frames.Count);
        Assert.False(frames[0].AsSpan().SequenceEqual(frames[1]));
        using var first = SKBitmap.Decode(frames[0]);
        Assert.Contains(
            first.Pixels,
            color => color.Red > 0xe0 && color.Red > color.Green + 40 && color.Red > color.Blue + 20);
    }
}
