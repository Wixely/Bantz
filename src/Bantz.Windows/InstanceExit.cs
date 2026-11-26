using System.Diagnostics;

namespace Bantz.Windows;

/// <summary>
/// Lets <c>Bantz --exit</c> quit the running interactive instance, as <c>Shade --exit</c> does.
/// Closing Bantz's window only hides it to the tray, so an updater such as Multitool has no other
/// way to stop it before replacing its files.
/// </summary>
internal static class InstanceExit
{
    // Session-local, like the single-instance lock, so a request never reaches another session.
    private const string EventName = @"Local\Bantz.InteractiveApp.Exit";

    /// <summary>Runs <paramref name="exit"/> once, on a pool thread, when an exit is requested.</summary>
    internal static IDisposable Listen(Action exit)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        var registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => exit(), null, Timeout.Infinite, executeOnlyOnce: true);
        return new Listener(signal, registration);
    }

    /// <summary>
    /// Asks the running instance to quit and waits for it to go. Exit codes match Shade's:
    /// 0 when it quit or was not running, 3 when it could not be reached in time.
    /// </summary>
    internal static int Request(string lockName, TimeSpan timeout)
    {
        if (!IsHeld(lockName))
        {
            return 0;
        }

        // A running build from before --exit existed has no event to signal.
        if (!EventWaitHandle.TryOpenExisting(EventName, out var signal))
        {
            return 3;
        }

        using (signal)
        {
            signal.Set();
        }

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (!IsHeld(lockName))
            {
                return 0;
            }

            Thread.Sleep(100);
        }

        return 3;
    }

    private static bool IsHeld(string lockName)
    {
        if (!Mutex.TryOpenExisting(lockName, out var mutex))
        {
            return false;
        }

        mutex.Dispose();
        return true;
    }

    private sealed class Listener(EventWaitHandle signal, RegisteredWaitHandle registration) : IDisposable
    {
        public void Dispose()
        {
            registration.Unregister(null);
            signal.Dispose();
        }
    }
}
