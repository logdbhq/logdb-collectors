using LogDB.Client.Models;
using LogDB.Extensions.Logging;

namespace com.logdb.windows.collector.Services;

/// <summary>
/// <see cref="ILogDBClient"/> wrapper that builds a fresh inner client for
/// every send and disposes it after. Same pattern the UI's Test button has
/// always used — it's the only collector-side path that reliably delivers
/// rows to grpc-logger when the long-lived singleton form goes zombie.
///
/// Background: a long-lived SDK client keeps one gRPC/HTTP-2 channel open
/// across all sends. Idle HTTP-2 connections can be silently dropped by an
/// intermediate NAT/load-balancer without a TCP RST — the client thinks the
/// channel is up, sends a frame into the void, and *some* hop returns
/// Status="Success" so the SDK reports success even though grpc-logger never
/// saw the row. Test never hits this because it tears the channel down after
/// every click and rebuilds it from scratch.
///
/// Cost is one TLS handshake (~100–300ms) per send. Acceptable for every
/// module the collector ships today (metrics every 5 min, IIS every minute,
/// EventLog every minute, Heartbeat every 60s). If a future high-volume
/// module needs connection pooling we'll add a refresh-on-N-sends variant —
/// but for now correctness beats throughput.
/// </summary>
internal sealed class EphemeralLogDbClient : ILogDBClient
{
    private readonly Func<ILogDBClient> _factory;

    public EphemeralLogDbClient(Func<ILogDBClient> factory)
    {
        _factory = factory;
    }

    public async Task<LogResponseStatus> LogAsync(Log log, CancellationToken cancellationToken = default)
    {
        var inner = _factory();
        try
        {
            var status = await inner.LogAsync(log, cancellationToken).ConfigureAwait(false);
            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            return status;
        }
        finally
        {
            DisposeQuietly(inner);
        }
    }

    public async Task<LogResponseStatus> LogBeatAsync(LogBeat logBeat, CancellationToken cancellationToken = default)
    {
        var inner = _factory();
        try
        {
            var status = await inner.LogBeatAsync(logBeat, cancellationToken).ConfigureAwait(false);
            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            return status;
        }
        finally
        {
            DisposeQuietly(inner);
        }
    }

    public async Task<LogResponseStatus> LogCacheAsync(LogCache logCache, CancellationToken cancellationToken = default)
    {
        var inner = _factory();
        try
        {
            var status = await inner.LogCacheAsync(logCache, cancellationToken).ConfigureAwait(false);
            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            return status;
        }
        finally
        {
            DisposeQuietly(inner);
        }
    }

    public async Task<LogResponseStatus> SendLogBatchAsync(IReadOnlyList<Log> logs, CancellationToken cancellationToken = default)
    {
        var inner = _factory();
        try
        {
            var status = await inner.SendLogBatchAsync(logs, cancellationToken).ConfigureAwait(false);
            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            return status;
        }
        finally
        {
            DisposeQuietly(inner);
        }
    }

    public async Task<LogResponseStatus> SendLogBeatBatchAsync(IReadOnlyList<LogBeat> logBeats, CancellationToken cancellationToken = default)
    {
        var inner = _factory();
        try
        {
            var status = await inner.SendLogBeatBatchAsync(logBeats, cancellationToken).ConfigureAwait(false);
            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            return status;
        }
        finally
        {
            DisposeQuietly(inner);
        }
    }

    public async Task<LogResponseStatus> SendLogCacheBatchAsync(IReadOnlyList<LogCache> logCaches, CancellationToken cancellationToken = default)
    {
        var inner = _factory();
        try
        {
            var status = await inner.SendLogCacheBatchAsync(logCaches, cancellationToken).ConfigureAwait(false);
            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            return status;
        }
        finally
        {
            DisposeQuietly(inner);
        }
    }

#pragma warning disable CS0618 // SDK marks LogPoint / LogRelation as [Obsolete] — re-implement to satisfy the interface; they throw the SDK's NotSupported.
    public Task<LogResponseStatus> LogPointAsync(LogPoint logPoint, CancellationToken cancellationToken = default)
    {
        var inner = _factory();
        try
        {
            return inner.LogPointAsync(logPoint, cancellationToken);
        }
        finally
        {
            DisposeQuietly(inner);
        }
    }

    public Task<LogResponseStatus> LogRelationAsync(LogRelation logRelation, CancellationToken cancellationToken = default)
    {
        var inner = _factory();
        try
        {
            return inner.LogRelationAsync(logRelation, cancellationToken);
        }
        finally
        {
            DisposeQuietly(inner);
        }
    }

    public Task<LogResponseStatus> SendLogPointBatchAsync(IReadOnlyList<LogPoint> logPoints, CancellationToken cancellationToken = default)
    {
        var inner = _factory();
        try
        {
            return inner.SendLogPointBatchAsync(logPoints, cancellationToken);
        }
        finally
        {
            DisposeQuietly(inner);
        }
    }

    public Task<LogResponseStatus> SendLogRelationBatchAsync(IReadOnlyList<LogRelation> logRelations, CancellationToken cancellationToken = default)
    {
        var inner = _factory();
        try
        {
            return inner.SendLogRelationBatchAsync(logRelations, cancellationToken);
        }
        finally
        {
            DisposeQuietly(inner);
        }
    }
#pragma warning restore CS0618

    // FlushAsync on the wrapper is a no-op: every method already flushes its
    // ephemeral inner client before disposal.
    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Dispose() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void DisposeQuietly(ILogDBClient inner)
    {
        try { inner.Dispose(); }
        catch { /* never let a dispose error mask a real send result */ }
    }
}
