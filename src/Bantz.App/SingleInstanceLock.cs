namespace Bantz;

/// <summary>Owns a named, process-wide lock for an interactive Bantz instance.</summary>
public sealed class SingleInstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    private bool _ownsMutex = true;

    private SingleInstanceLock(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static SingleInstanceLock? TryAcquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var mutex = new Mutex(initiallyOwned: false, name);
        try
        {
            try
            {
                if (!mutex.WaitOne(0))
                {
                    mutex.Dispose();
                    return null;
                }
            }
            catch (AbandonedMutexException)
            {
                // The previous process ended without releasing the mutex; this process now owns it.
            }

            return new SingleInstanceLock(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (!_ownsMutex)
        {
            return;
        }

        _ownsMutex = false;
        try
        {
            _mutex.ReleaseMutex();
        }
        finally
        {
            _mutex.Dispose();
        }
    }
}
