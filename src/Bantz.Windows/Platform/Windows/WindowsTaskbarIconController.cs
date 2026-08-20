using System.Runtime.InteropServices;

namespace Bantz.Platform.Windows;

public sealed partial class WindowsTaskbarIconController : IDisposable
{
    private const uint WindowIconMessage = 0x0080;
    private const nuint SmallIcon = 0;
    private const nuint BigIcon = 1;
    private const uint IconResourceVersion = 0x00030000;
    private const uint GetWindowOwner = 4;
    private const uint NotifyIconModify = 1;
    private const uint NotifyIconFlagIcon = 0x00000002;
    private const uint TrayIconId = 1;

    private nint _largeIcon;
    private nint _smallIcon;

    public void Update(byte[] pngBytes)
    {
        var window = FindApplicationWindow();
        if (window == 0)
        {
            return;
        }

        var largeIcon = CreateIconFromResourceEx(
            pngBytes,
            (uint)pngBytes.Length,
            true,
            IconResourceVersion,
            32,
            32,
            0);
        var smallIcon = CreateIconFromResourceEx(
            pngBytes,
            (uint)pngBytes.Length,
            true,
            IconResourceVersion,
            16,
            16,
            0);
        if (largeIcon == 0 || smallIcon == 0)
        {
            if (largeIcon != 0) _ = DestroyIcon(largeIcon);
            if (smallIcon != 0) _ = DestroyIcon(smallIcon);
            return;
        }

        _ = SendMessageW(window, WindowIconMessage, BigIcon, largeIcon);
        _ = SendMessageW(window, WindowIconMessage, SmallIcon, smallIcon);
        var trayIconData = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = window,
            Id = TrayIconId,
            Flags = NotifyIconFlagIcon,
            Icon = smallIcon,
            Tip = string.Empty,
            Info = string.Empty,
            InfoTitle = string.Empty,
        };
        _ = ShellNotifyIconW(NotifyIconModify, ref trayIconData);
        DestroyCurrentIcons();
        _largeIcon = largeIcon;
        _smallIcon = smallIcon;
    }

    public void Dispose()
    {
        DestroyCurrentIcons();
        GC.SuppressFinalize(this);
    }

    private static nint FindApplicationWindow()
    {
        nint result = 0;
        var processId = (uint)Environment.ProcessId;
        EnumWindowProcedure procedure = (window, parameter) =>
        {
            _ = GetWindowThreadProcessId(window, out var candidateProcessId);
            if (candidateProcessId != processId || !IsWindowVisible(window) || GetWindow(window, GetWindowOwner) != 0)
            {
                return true;
            }

            result = window;
            return false;
        };
        _ = EnumWindows(procedure, 0);
        GC.KeepAlive(procedure);
        return result;
    }

    private void DestroyCurrentIcons()
    {
        if (_largeIcon != 0)
        {
            _ = DestroyIcon(_largeIcon);
            _largeIcon = 0;
        }

        if (_smallIcon != 0)
        {
            _ = DestroyIcon(_smallIcon);
            _smallIcon = 0;
        }
    }

    private delegate bool EnumWindowProcedure(nint window, nint parameter);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIcon;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowProcedure procedure, nint parameter);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint window, uint command);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nint CreateIconFromResourceEx(
        [In] byte[] resourceBits,
        uint resourceSize,
        [MarshalAs(UnmanagedType.Bool)] bool icon,
        uint version,
        int desiredWidth,
        int desiredHeight,
        uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIconW(uint message, ref NotifyIconData data);
}
