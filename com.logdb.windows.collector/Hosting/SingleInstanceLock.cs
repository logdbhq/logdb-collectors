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
        var hasHandle = false;

        try
        {
            hasHandle = mutex.WaitOne(TimeSpan.Zero, false);
            return new SingleInstanceLock(mutex, hasHandle);
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
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
