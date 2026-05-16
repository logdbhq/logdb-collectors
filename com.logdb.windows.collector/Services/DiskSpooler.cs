namespace com.logdb.windows.collector.Services;

public interface IDiskSpooler
{
    Task EnqueueAsync(string payload, CancellationToken cancellationToken = default);
}

public sealed class NullDiskSpooler : IDiskSpooler
{
    public Task EnqueueAsync(string payload, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
