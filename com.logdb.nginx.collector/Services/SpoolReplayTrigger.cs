namespace com.logdb.nginx.collector.Services;

/// <summary>
/// Lets the UI wake the <see cref="SpoolReplayWorker"/> immediately instead of
/// waiting out the flush interval. The worker waits on this between cycles, so a
/// manual flush still runs through the single replay loop (no concurrent spool
/// reads / double-sends).
/// </summary>
public class SpoolReplayTrigger
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Request an immediate replay cycle. No-op if one is already queued.</summary>
    public void RequestFlush()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* a flush is already pending */ }
    }

    /// <summary>
    /// Wait until either the interval elapses or a manual flush is requested.
    /// Returns true if woken by a manual request, false on timeout.
    /// </summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _wake.WaitAsync(timeout, ct);
}
