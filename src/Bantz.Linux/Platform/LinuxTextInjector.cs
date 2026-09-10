using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Bantz.Core;

namespace Bantz.Platform.Linux;

public sealed class LinuxTextInjector : ITextInjector
{
    // Pasting is asynchronous from here: the destination reads the clipboard on its own event
    // loop, so the previous contents can only go back once it has had a moment to do that.
    private const int PasteSettleMilliseconds = 250;
    private const int MaximumPreservedBytes = 32 * 1024 * 1024;

    // Selection bookkeeping that X11 advertises alongside the real content types.
    private static readonly string[] SelectionMetadataTargets =
        ["TARGETS", "TIMESTAMP", "MULTIPLE", "SAVE_TARGETS", "DELETE", "INSERT_SELECTION", "INSERT_PROPERTY"];

    private static readonly string[] PreferredTypes =
        ["text/plain;charset=utf-8", "text/plain", "UTF8_STRING", "STRING", "TEXT"];

    private readonly Func<bool> _useClipboardPaste;

    public LinuxTextInjector()
        : this(null)
    {
    }

    /// <summary>
    /// Creates an injector that reads <paramref name="useClipboardPaste"/> for each transcript, so
    /// the compatibility setting can change while the application runs. Clipboard paste suits
    /// destinations that drop synthesized keystrokes, such as remote-desktop and terminal clients.
    /// </summary>
    public LinuxTextInjector(Func<bool>? useClipboardPaste)
    {
        _useClipboardPaste = useClipboardPaste ?? (static () => false);
    }

    public TextInjectionResult InjectIntoForeground(string text, bool pressEnter)
    {
        var wayland = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        var candidates = wayland
            ? new[] { InjectorKind.Wtype, InjectorKind.Xdotool }
            : new[] { InjectorKind.Xdotool, InjectorKind.Wtype };

        if (!_useClipboardPaste())
        {
            foreach (var candidate in candidates)
            {
                var result = TryInject(candidate, text, pressEnter);
                if (result is not null)
                {
                    return result.Value;
                }
            }

            return TextInjectionResult.Failure(
                "Linux text injection needs 'wtype' on Wayland or 'xdotool' on X11. The transcript remains visible in Bantz.");
        }

        var previousContents = CaptureClipboard(wayland);
        if (!TryWriteClipboard(Encoding.UTF8.GetBytes(text), type: null, wayland))
        {
            return TextInjectionResult.Failure(
                "Clipboard paste needs 'wl-copy' on Wayland or 'xclip' on X11. The transcript remains visible in Bantz.");
        }

        try
        {
            foreach (var candidate in candidates)
            {
                var result = TryPaste(candidate, pressEnter);
                if (result is not null)
                {
                    return result.Value;
                }
            }

            return TextInjectionResult.Failure(
                "Linux text injection needs 'wtype' on Wayland or 'xdotool' on X11. The transcript remains visible in Bantz.");
        }
        finally
        {
            RestoreClipboard(previousContents, wayland);
        }
    }

    /// <summary>
    /// Reads back what the clipboard is offering so the paste can hand it over afterwards.
    /// Unlike Windows, only one type can be preserved: <c>wl-copy</c> and <c>xclip</c> each own the
    /// selection for a single type per invocation, so text is kept when the clipboard offers it and
    /// otherwise the first type it advertises.
    /// </summary>
    private static ClipboardContents? CaptureClipboard(bool wayland)
    {
        foreach (var tool in ClipboardOrder(wayland))
        {
            var listing = RunClipboardTool(tool, ListTypeArguments(tool), input: null);
            if (listing is null)
            {
                continue;
            }

            var types = Encoding.UTF8.GetString(listing)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(candidate => Array.IndexOf(SelectionMetadataTargets, candidate) < 0)
                .ToArray();
            if (types.Length == 0)
            {
                return null;
            }

            var type = PreferredTypes.FirstOrDefault(types.Contains) ?? types[0];
            var data = RunClipboardTool(tool, ReadArguments(tool, type), input: null);
            return data is { Length: > 0 } and { Length: <= MaximumPreservedBytes }
                ? new ClipboardContents(type, data)
                : null;
        }

        return null;
    }

    /// <summary>Puts the captured clipboard type back once the destination has had time to paste.</summary>
    private static void RestoreClipboard(ClipboardContents? previousContents, bool wayland)
    {
        if (previousContents is null)
        {
            return;
        }

        Thread.Sleep(PasteSettleMilliseconds);
        _ = TryWriteClipboard(previousContents.Data, previousContents.Type, wayland);
    }

    private static ClipboardKind[] ClipboardOrder(bool wayland) => wayland
        ? [ClipboardKind.WlCopy, ClipboardKind.Xclip]
        : [ClipboardKind.Xclip, ClipboardKind.WlCopy];

    private static string[] ListTypeArguments(ClipboardKind tool) => tool == ClipboardKind.WlCopy
        ? ["--list-types"]
        : ["-selection", "clipboard", "-o", "-t", "TARGETS"];

    private static string[] ReadArguments(ClipboardKind tool, string type) => tool == ClipboardKind.WlCopy
        ? ["--type", type, "--no-newline"]
        : ["-selection", "clipboard", "-o", "-t", type];

    private static string[] WriteArguments(ClipboardKind tool, string? type) => (tool, type) switch
    {
        (ClipboardKind.WlCopy, null) => [],
        (ClipboardKind.WlCopy, _) => ["--type", type],
        (_, null) => ["-selection", "clipboard"],
        _ => ["-selection", "clipboard", "-t", type],
    };

    /// <summary>Runs a clipboard tool, returning its raw output, or null when it is unavailable.</summary>
    private static byte[]? RunClipboardTool(ClipboardKind tool, string[] arguments, byte[]? input)
    {
        var reading = input is null;
        var start = new ProcessStartInfo
        {
            // Reads use wl-paste; writes use wl-copy. xclip does both.
            FileName = tool == ClipboardKind.Xclip ? "xclip" : reading ? "wl-paste" : "wl-copy",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = !reading,
            RedirectStandardOutput = reading,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            if (reading)
            {
                using var output = new MemoryStream();
                process.StandardOutput.BaseStream.CopyTo(output);
                process.WaitForExit();
                return process.ExitCode == 0 ? output.ToArray() : null;
            }

            process.StandardInput.BaseStream.Write(input!, 0, input!.Length);
            process.StandardInput.BaseStream.Flush();
            process.StandardInput.Close();
            process.WaitForExit();
            return process.ExitCode == 0 ? [] : null;
        }
        catch (Win32Exception)
        {
            // That clipboard tool is not installed; the caller tries the other one.
            return null;
        }
        catch (IOException)
        {
            // The tool exited before taking the data.
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Places bytes on the clipboard, optionally under a specific MIME type.</summary>
    private static bool TryWriteClipboard(byte[] data, string? type, bool wayland)
    {
        foreach (var tool in ClipboardOrder(wayland))
        {
            if (RunClipboardTool(tool, WriteArguments(tool, type), data) is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static TextInjectionResult? TryPaste(InjectorKind kind, bool pressEnter)
    {
        var command = kind == InjectorKind.Wtype ? "wtype" : "xdotool";
        var start = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        if (kind == InjectorKind.Wtype)
        {
            start.ArgumentList.Add("-M");
            start.ArgumentList.Add("ctrl");
            start.ArgumentList.Add("-k");
            start.ArgumentList.Add("v");
            start.ArgumentList.Add("-m");
            start.ArgumentList.Add("ctrl");
        }
        else
        {
            start.ArgumentList.Add("key");
            start.ArgumentList.Add("--clearmodifiers");
            start.ArgumentList.Add("ctrl+v");
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return TextInjectionResult.Failure(
                    $"{command} could not paste the transcript: {process.StandardError.ReadToEnd().Trim()}");
            }

            return pressEnter ? PressEnter(kind, command) : TextInjectionResult.Success();
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException exception)
        {
            return TextInjectionResult.Failure($"Linux could not paste the transcript: {exception.Message}");
        }
    }

    private static TextInjectionResult? TryInject(InjectorKind kind, string text, bool pressEnter)
    {
        var command = kind == InjectorKind.Wtype ? "wtype" : "xdotool";
        var start = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        if (kind == InjectorKind.Wtype)
        {
            start.ArgumentList.Add("--");
            start.ArgumentList.Add(text);
        }
        else
        {
            start.ArgumentList.Add("type");
            start.ArgumentList.Add("--clearmodifiers");
            start.ArgumentList.Add("--delay");
            start.ArgumentList.Add("0");
            start.ArgumentList.Add("--");
            start.ArgumentList.Add(text);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return TextInjectionResult.Failure(
                    $"{command} could not type the transcript: {process.StandardError.ReadToEnd().Trim()}");
            }

            return pressEnter ? PressEnter(kind, command) : TextInjectionResult.Success();
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException exception)
        {
            return TextInjectionResult.Failure($"Linux could not type the transcript: {exception.Message}");
        }
    }

    private static TextInjectionResult PressEnter(InjectorKind kind, string command)
    {
        var enter = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        if (kind == InjectorKind.Wtype)
        {
            enter.ArgumentList.Add("-k");
            enter.ArgumentList.Add("Return");
        }
        else
        {
            enter.ArgumentList.Add("key");
            enter.ArgumentList.Add("Return");
        }

        using var enterProcess = Process.Start(enter);
        enterProcess?.WaitForExit();
        return enterProcess is null || enterProcess.ExitCode != 0
            ? TextInjectionResult.Failure($"{command} delivered the transcript but could not press Enter.")
            : TextInjectionResult.Success();
    }

    private enum InjectorKind
    {
        Wtype,
        Xdotool,
    }

    private enum ClipboardKind
    {
        WlCopy,
        Xclip,
    }

    private sealed record ClipboardContents(string Type, byte[] Data);
}
