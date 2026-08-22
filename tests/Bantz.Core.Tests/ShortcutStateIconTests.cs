using Bantz.Ui;
using SkiaSharp;
using Xunit;

namespace Bantz.Core.Tests;

public sealed class ShortcutStateIconTests
{
    [Fact]
    public void DisabledIconChangesMintPixelsToOrangeAndPreservesCoralPixels()
    {
        using var source = new SKBitmap(2, 1);
        source.SetPixel(0, 0, new SKColor(0x68, 0xe0, 0xb1));
        source.SetPixel(1, 0, new SKColor(0xff, 0x78, 0x68));
        using var image = SKImage.FromBitmap(source);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        var resultBytes = ShortcutStateIcon.CreateDisabled(encoded.ToArray());
        using var result = SKBitmap.Decode(resultBytes);

        var orange = result.GetPixel(0, 0);
        Assert.True(orange.Red > orange.Green);
        Assert.True(orange.Green > orange.Blue);
        Assert.Equal(source.GetPixel(1, 0), result.GetPixel(1, 0));
    }
}
