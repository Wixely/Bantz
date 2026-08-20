using System.ComponentModel;
using System.Diagnostics;
using Bantz.Core;

namespace Bantz.Platform.Linux;

public sealed class LinuxTextInjector : ITextInjector
{
    public TextInjectionResult InjectIntoForeground(string text, bool pressEnter)
    {
        var candidates = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            ? new[] { InjectorKind.Wtype, InjectorKind.Xdotool }
            : new[] { InjectorKind.Xdotool, InjectorKind.Wtype };

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

            if (pressEnter)
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
                if (enterProcess is null || enterProcess.ExitCode != 0)
                {
                    return TextInjectionResult.Failure($"{command} typed the transcript but could not press Enter.");
                }
            }

            return TextInjectionResult.Success();
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

    private enum InjectorKind
    {
        Wtype,
        Xdotool,
    }
}
