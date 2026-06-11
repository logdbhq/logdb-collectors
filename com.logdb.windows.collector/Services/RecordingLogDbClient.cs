using com.logdb.windows.collector.Activity;
using LogDB.Client.Models;
using LogDB.Extensions.Logging;

namespace com.logdb.windows.collector.Services;

/// <summary>
/// <see cref="ILogDBClient"/> decorator that records every send into an
/// <see cref="ISendActivitySink"/> (tagged with the owning module + host and
/// split by outcome) before delegating to the wrapped client. Wrapping at the
/// client boundary captures throughput uniformly for every module without
/// touching the shared exporter libraries.
///
/// Wrap order per module: RecordingLogDbClient → EphemeralLogDbClient → real SDK client.
/// </summary>
internal sealed class RecordingLogDbClient : ILogDBClient
{
    private readonly ILogDBClient _inner;
    private readonly ISendActivitySink _sink;
    private readonly string _module;
    private readonly string _host;

    public RecordingLogDbClient(ILogDBClient inner, ISendActivitySink sink, string module, string host)
    {
        _inner = inner;
        _sink = sink;
        _module = module;
        _host = host;
    }

    public async Task<LogResponseStatus> LogAsync(Log log, CancellationToken cancellationToken = default)
    {
        var status = await _inner.LogAsync(log, cancellationToken).ConfigureAwait(false);
        Record(log?.Collection, 1, status);
        return status;
    }

    public async Task<LogResponseStatus> SendLogBatchAsync(IReadOnlyList<Log> logs, CancellationToken cancellationToken = default)
    {
        var status = await _inner.SendLogBatchAsync(logs, cancellationToken).ConfigureAwait(false);
        Record(logs is { Count: > 0 } ? logs[0]?.Collection : null, logs?.Count ?? 0, status);
        return status;
    }

    public async Task<LogResponseStatus> LogBeatAsync(LogBeat logBeat, CancellationToken cancellationToken = default)
    {
        var status = await _inner.LogBeatAsync(logBeat, cancellationToken).ConfigureAwait(false);
        Record(logBeat?.Collection, 1, status);
        return status;
    }

    public async Task<LogResponseStatus> SendLogBeatBatchAsync(IReadOnlyList<LogBeat> logBeats, CancellationToken cancellationToken = default)
    {
        var status = await _inner.SendLogBeatBatchAsync(logBeats, cancellationToken).ConfigureAwait(false);
        Record(logBeats is { Count: > 0 } ? logBeats[0]?.Collection : null, logBeats?.Count ?? 0, status);
        return status;
    }

    public async Task<LogResponseStatus> LogCacheAsync(LogCache logCache, CancellationToken cancellationToken = default)
    {
        var status = await _inner.LogCacheAsync(logCache, cancellationToken).ConfigureAwait(false);
        Record(logCache?.Collection, 1, status);
        return status;
    }

    public async Task<LogResponseStatus> SendLogCacheBatchAsync(IReadOnlyList<LogCache> logCaches, CancellationToken cancellationToken = default)
    {
        var status = await _inner.SendLogCacheBatchAsync(logCaches, cancellationToken).ConfigureAwait(false);
        Record(logCaches is { Count: > 0 } ? logCaches[0]?.Collection : null, logCaches?.Count ?? 0, status);
        return status;
    }

    private void Record(string? collection, long count, LogResponseStatus status)
    {
        if (count <= 0) return;
        try
        {
            _sink.Record(_module, collection, _host, count, status == LogResponseStatus.Success, DateTime.UtcNow);
        }
        catch
        {
            // Telemetry must never affect delivery.
        }
    }

    // LogPoint / LogRelation are disabled in the SDK — delegate without recording.
#pragma warning disable CS0618
    public Task<LogResponseStatus> LogPointAsync(LogPoint logPoint, CancellationToken cancellationToken = default) =>
        _inner.LogPointAsync(logPoint, cancellationToken);

    public Task<LogResponseStatus> LogRelationAsync(LogRelation logRelation, CancellationToken cancellationToken = default) =>
        _inner.LogRelationAsync(logRelation, cancellationToken);

    public Task<LogResponseStatus> SendLogPointBatchAsync(IReadOnlyList<LogPoint> logPoints, CancellationToken cancellationToken = default) =>
        _inner.SendLogPointBatchAsync(logPoints, cancellationToken);

    public Task<LogResponseStatus> SendLogRelationBatchAsync(IReadOnlyList<LogRelation> logRelations, CancellationToken cancellationToken = default) =>
        _inner.SendLogRelationBatchAsync(logRelations, cancellationToken);
#pragma warning restore CS0618

    public Task FlushAsync(CancellationToken cancellationToken = default) => _inner.FlushAsync(cancellationToken);

    public void Dispose() => _inner.Dispose();

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
