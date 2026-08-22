namespace Bantz.Core;

public readonly record struct InitialWindowSize(int Width, int Height)
{
    public static InitialWindowSize FitWithinWorkArea(
        InitialWindowSize preferred,
        int workAreaWidth,
        int workAreaHeight,
        int reservedWidth = 32,
        int reservedHeight = 64)
    {
        if (preferred.Width <= 0 || preferred.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(preferred), "The preferred window size must be positive.");
        }

        if (workAreaWidth <= 0 || workAreaHeight <= 0)
        {
            return preferred;
        }

        var availableWidth = Math.Max(1, workAreaWidth - Math.Max(0, reservedWidth));
        var availableHeight = Math.Max(1, workAreaHeight - Math.Max(0, reservedHeight));
        var scale = Math.Min(
            1d,
            Math.Min(
                availableWidth / (double)preferred.Width,
                availableHeight / (double)preferred.Height));

        return new InitialWindowSize(
            Math.Max(1, (int)Math.Floor(preferred.Width * scale)),
            Math.Max(1, (int)Math.Floor(preferred.Height * scale)));
    }
}
