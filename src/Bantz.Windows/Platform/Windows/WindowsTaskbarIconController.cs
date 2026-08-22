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
    private const uint NotifyIconFlagTip = 0x00000004;
    private const uint SendMessageAbortIfHung = 0x0002;
    private const uint TrayIconId = 1;

    private readonly object _sync = new();
    private nint _largeIcon;
    private nint _smallIcon;
    private Timer? _animationTimer;
    private IReadOnlyList<byte[]>? _animationFrames;
    private int _animationFrame;
    private bool _disposed;

    public void SetIdleIcon(byte[] pngBytes)
    {
        lock (_sync)
        {
            if (_disposed || _animationTimer is not null)
            {
                return;
            }

            Update(pngBytes, "Bantz");
        }
    }

    public void StartRecording(IReadOnlyList<byte[]> frames)
    {
        ArgumentOutOfRangeException.ThrowIfZero(frames.Count);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _animationTimer?.Dispose();
            _animationFrames = frames;
            _animationFrame = 0;
            Update(frames[0], "Bantz is recording");
            _animationTimer = new Timer(AdvanceRecordingFrame, null, 150, 150);
        }
    }

    public void StopRecording(byte[] idleIcon)
    {
        Timer? timer;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            timer = _animationTimer;
            _animationTimer = null;
            _animationFrames = null;
            Update(idleIcon, "Bantz");
        }

        timer?.Dispose();
    }

    private void AdvanceRecordingFrame(object? state)
    {
        lock (_sync)
        {
            if (_disposed || _animationTimer is null || _animationFrames is not { Count: > 0 } frames)
            {
                return;
            }

            _animationFrame = (_animationFrame + 1) % frames.Count;
            Update(frames[_animationFrame], "Bantz is recording");
        }
    }

    private void Update(byte[] pngBytes, string tooltip)
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

        _ = SendMessageTimeoutW(window, WindowIconMessage, BigIcon, largeIcon, SendMessageAbortIfHung, 250, out _);
        _ = SendMessageTimeoutW(window, WindowIconMessage, SmallIcon, smallIcon, SendMessageAbortIfHung, 250, out _);
        var trayIconData = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = window,
            Id = TrayIconId,
            Flags = NotifyIconFlagIcon | NotifyIconFlagTip,
            Icon = smallIcon,
            Tip = tooltip,
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
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _animationTimer?.Dispose();
            _animationTimer = null;
            _animationFrames = null;
            DestroyCurrentIcons();
        }

        GC.SuppressFinalize(this);
    }

    private static nint FindApplicationWindow()
    {
        nint result = 0;
        var processId = (uint)Environment.ProcessId;
        EnumWindowProcedure procedure = (window, parameter) =>
        {
            _ = GetWindowThreadProcessId(window, out var candidateProcessId);
            // A close-to-tray window is intentionally hidden but still owns the notification icon.
            if (candidateProcessId != processId || GetWindow(window, GetWindowOwner) != 0)
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
    private static partial nint SendMessageTimeoutW(
        nint window,
        uint message,
        nuint wParam,
        nint lParam,
        uint flags,
        uint timeoutMilliseconds,
        out nuint result);

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
