using Bantz.Core;
using Silk.NET.Maths;
using Silk.NET.SDL;

namespace Bantz.Platform.Linux;

internal static class LinuxDisplayWorkArea
{
    private const int FallbackWorkAreaWidth = 1024;
    private const int FallbackWorkAreaHeight = 600;

    public static InitialWindowSize FitInitialWindow(InitialWindowSize preferred)
    {
        try
        {
            var sdl = Sdl.GetApi();
            if (sdl.Init(Sdl.InitVideo) != 0)
            {
                return Fallback(preferred);
            }

            try
            {
                var displayIndex = DisplayUnderPointer(sdl);
                var workArea = default(Rectangle<int>);
                if (sdl.GetDisplayUsableBounds(displayIndex, ref workArea) != 0)
                {
                    return Fallback(preferred);
                }

                return InitialWindowSize.FitWithinWorkArea(
                    preferred,
                    workArea.Size.X,
                    workArea.Size.Y);
            }
            finally
            {
                sdl.Quit();
            }
        }
        catch (DllNotFoundException)
        {
            return Fallback(preferred);
        }
        catch (EntryPointNotFoundException)
        {
            return Fallback(preferred);
        }
        catch (PlatformNotSupportedException)
        {
            return Fallback(preferred);
        }
    }

    private static int DisplayUnderPointer(Sdl sdl)
    {
        var x = 0;
        var y = 0;
        _ = sdl.GetGlobalMouseState(ref x, ref y);
        var point = new Point
        {
            X = x,
            Y = y,
        };
        var displayIndex = sdl.GetPointDisplayIndex(ref point);
        return displayIndex >= 0 ? displayIndex : 0;
    }

    private static InitialWindowSize Fallback(InitialWindowSize preferred) =>
        InitialWindowSize.FitWithinWorkArea(
            preferred,
            FallbackWorkAreaWidth,
            FallbackWorkAreaHeight);
}
