using System.Runtime.InteropServices;
using Bantz.Core;

namespace Bantz.Platform.Windows;

internal static partial class WindowsDisplayWorkArea
{
    private const uint MonitorDefaultToPrimary = 1;
    private const int PrimaryScreenWidth = 0;
    private const int PrimaryScreenHeight = 1;
    private const int FallbackWorkAreaWidth = 1024;
    private const int FallbackWorkAreaHeight = 600;

    public static InitialWindowSize FitInitialWindow(InitialWindowSize preferred)
    {
        _ = GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, MonitorDefaultToPrimary);
        if (monitor != 0)
        {
            var information = new MonitorInformation
            {
                Size = (uint)Marshal.SizeOf<MonitorInformation>(),
            };
            if (GetMonitorInfoW(monitor, ref information))
            {
                return InitialWindowSize.FitWithinWorkArea(
                    preferred,
                    information.Work.Right - information.Work.Left,
                    information.Work.Bottom - information.Work.Top);
            }
        }

        var width = GetSystemMetrics(PrimaryScreenWidth);
        var height = GetSystemMetrics(PrimaryScreenHeight);
        return InitialWindowSize.FitWithinWorkArea(
            preferred,
            width > 0 ? width : FallbackWorkAreaWidth,
            height > 0 ? height : FallbackWorkAreaHeight);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInformation
    {
        public uint Size;
        public Rectangle Monitor;
        public Rectangle Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(Point point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInformation information);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
}
