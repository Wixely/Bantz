using System.Diagnostics;
using System.Runtime.InteropServices;
using Bantz.Platform.Windows;

namespace Bantz.Windows;

/// <summary>
/// Drives compatibility mode against a real window and reads back what arrived, so the paste can be
/// verified rather than assumed. Notepad is the target because its text can be read directly out of
/// its edit control with WM_GETTEXT — asking for it through the clipboard would be testing the
/// mechanism under test with itself.
/// </summary>
internal static partial class PasteSelfTest
{
    private const uint WindowMessageGetText = 0x000D;
    private const uint WindowMessageGetTextLength = 0x000E;

    public static void Run(string logPath)
    {
        var expected = $"BANTZ-SELFTEST-{DateTime.UtcNow:HHmmss}";
        var log = new List<string> { $"expecting: {expected}" };
        Process? notepad = null;
        try
        {
            notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })
                ?? throw new InvalidOperationException("Notepad did not start.");

            var window = nint.Zero;
            for (var waited = 0; waited < 5_000 && window == nint.Zero; waited += 100)
            {
                Thread.Sleep(100);
                notepad.Refresh();
                window = notepad.MainWindowHandle;
            }

            if (window == nint.Zero)
            {
                log.Add("FAILED: Notepad never showed a window.");
                return;
            }

            _ = SetForegroundWindow(window);
            Thread.Sleep(400);
            log.Add($"foreground is notepad: {GetForegroundWindow() == window}");

            var injector = new WindowsTextInjector(static () => true);
            var result = injector.InjectIntoForeground(expected, pressEnter: false);
            log.Add($"inject reported: {(result.Succeeded ? "ok" : result.Error)}");

            // The paste is asynchronous and the clipboard is held for a couple of seconds after it.
            Thread.Sleep(4_000);

            var edit = FindWindowExW(window, nint.Zero, "Edit", null);
            if (edit == nint.Zero)
            {
                log.Add("FAILED: could not find Notepad's edit control (Windows 11 Notepad?).");
                return;
            }

            var length = (int)SendMessageW(edit, WindowMessageGetTextLength, nint.Zero, nint.Zero);
            var buffer = new char[length + 1];
            int copied;
            unsafe
            {
                fixed (char* text = buffer)
                {
                    copied = (int)SendMessageW(edit, WindowMessageGetText, buffer.Length, (nint)text);
                }
            }

            var actual = new string(buffer, 0, Math.Clamp(copied, 0, buffer.Length));

            log.Add($"notepad contains: '{actual}'");
            log.Add(string.Equals(actual, expected, StringComparison.Ordinal)
                ? "PASSED: the paste arrived intact."
                : "FAILED: the paste did not arrive.");
        }
        catch (Exception exception)
        {
            log.Add($"FAILED: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            try
            {
                // Killed rather than closed, so nothing asks to save the pasted text.
                notepad?.Kill();
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            File.WriteAllLines(logPath, log);
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(nint parent, nint child, string? className, string? windowName);

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint window, uint message, int wParam, nint text);
}
