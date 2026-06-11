namespace com.logdb.windows.collector.Hosting;

public sealed class SingleInstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    private readonly bool _hasHandle;

    private SingleInstanceLock(Mutex mutex, bool hasHandle)
    {
        _mutex = mutex;
        _hasHandle = hasHandle;
    }

    public bool HasHandle => _hasHandle;

    public static SingleInstanceLock Acquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: false, name);

        try
        {
            var hasHandle = mutex.WaitOne(TimeSpan.Zero, false);
            return new SingleInstanceLock(mutex, hasHandle);
        }
        catch (AbandonedMutexException)
        {
            // A previous instance exited without cleanly releasing the mutex (a
            // crash, or an async Dispose that couldn't release from the owning
            // thread). Ownership still transfers to us — treat it as acquired.
            return new SingleInstanceLock(mutex, hasHandle: true);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_hasHandle)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (Exception)
            {
                // A Mutex can only be released by the thread that acquired it. In an
                // async Main, Dispose() can run on a different thread-pool thread, so
                // ReleaseMutex throws ApplicationException. Don't let process cleanup
                // crash the process (which would write Application Error / .NET
                // Runtime events to the Windows event log) — closing the handle below
                // releases the mutex for the next instance regardless.
            }
        }

        _mutex.Dispose();
    }
}
