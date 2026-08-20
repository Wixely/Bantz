using SkiaSharp;

namespace Bantz.Ui;

public static class ShortcutStateIcon
{
    public static byte[] CreateDisabled(byte[] sourceBytes)
    {
        using var bitmap = SKBitmap.Decode(sourceBytes)
            ?? throw new InvalidDataException("The application icon is not a readable image.");

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                if (color.Green <= color.Red + 35 || color.Green <= color.Blue + 20)
                {
                    continue;
                }

                var maximum = Math.Max(color.Red, Math.Max(color.Green, color.Blue)) / 255f;
                var minimum = Math.Min(color.Red, Math.Min(color.Green, color.Blue)) / 255f;
                var saturation = maximum <= 0 ? 0 : Math.Min(1f, ((maximum - minimum) / maximum) * 1.25f);
                var chroma = maximum * saturation;
                var midpoint = chroma * 0.5f;
                var offset = maximum - chroma;
                bitmap.SetPixel(x, y, new SKColor(
                    ToByte(chroma + offset),
                    ToByte(midpoint + offset),
                    ToByte(offset),
                    color.Alpha));
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}
