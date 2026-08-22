using SkiaSharp;

namespace Bantz.Ui;

public static class RecordingStateIcon
{
    public const int FrameCount = 4;

    public static IReadOnlyList<byte[]> CreateFrames(byte[] sourceBytes)
    {
        using var source = SKBitmap.Decode(sourceBytes)
            ?? throw new InvalidDataException("The application icon is not a readable image.");
        var frames = new byte[FrameCount][];

        for (var frame = 0; frame < frames.Length; frame++)
        {
            using var bitmap = source.Copy();
            using var canvas = new SKCanvas(bitmap);
            DrawEqualizerBadge(canvas, bitmap.Width, bitmap.Height, frame);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            frames[frame] = encoded.ToArray();
        }

        return frames;
    }

    private static void DrawEqualizerBadge(SKCanvas canvas, int width, int height, int frame)
    {
        var diameter = MathF.Max(18, MathF.Min(width, height) * 0.58f);
        var radius = diameter / 2;
        var center = new SKPoint(width - radius, height - radius);
        using var background = new SKPaint { Color = new SKColor(0x24, 0x14, 0x17, 0xf2), IsAntialias = true };
        using var outline = new SKPaint
        {
            Color = new SKColor(0xff, 0x78, 0x68),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(1.5f, diameter * 0.055f),
        };
        using var bars = new SKPaint { Color = new SKColor(0xff, 0x91, 0x7f), IsAntialias = true };
        canvas.DrawCircle(center, radius - outline.StrokeWidth, background);
        canvas.DrawCircle(center, radius - outline.StrokeWidth, outline);

        var barWidth = diameter * 0.085f;
        var gap = diameter * 0.055f;
        var totalWidth = (barWidth * 4) + (gap * 3);
        var left = center.X - (totalWidth / 2);
        var baseline = center.Y + (diameter * 0.22f);
        for (var bar = 0; bar < 4; bar++)
        {
            var barHeight = diameter * BarHeight(frame, bar);
            var rectangle = new SKRect(
                left + (bar * (barWidth + gap)),
                baseline - barHeight,
                left + (bar * (barWidth + gap)) + barWidth,
                baseline);
            canvas.DrawRoundRect(rectangle, barWidth / 2, barWidth / 2, bars);
        }
    }

    private static float BarHeight(int frame, int bar) => (frame, bar) switch
    {
        (0, 0) or (2, 2) => 0.32f,
        (0, 1) or (2, 3) => 0.62f,
        (0, 2) or (2, 0) => 0.48f,
        (0, 3) or (2, 1) => 0.72f,
        (1, 0) or (3, 2) => 0.68f,
        (1, 1) or (3, 3) => 0.38f,
        (1, 2) or (3, 0) => 0.74f,
        _ => 0.5f,
    };
}
