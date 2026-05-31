using com.logdb.nginx.collector.Configuration;
using com.logdb.nginx.collector.Models;
using Microsoft.Extensions.Options;

namespace com.logdb.nginx.collector.Services;

public class SpoolReplayWorker : BackgroundService
{
    private readonly ILogger<SpoolReplayWorker> _logger;
    private readonly ISpoolStore _spool;
    private readonly ILogDbExporter _exporter;
    private readonly SpoolOptions _options;
    private readonly SpoolReplayState _state;
    private readonly SpoolReplayTrigger _trigger;

    public SpoolReplayWorker(ILogger<SpoolReplayWorker> logger, ISpoolStore spool, ILogDbExporter exporter, IOptions<SpoolOptions> options, SpoolReplayState state, SpoolReplayTrigger trigger)
    {
        _logger = logger;
        _spool = spool;
        _exporter = exporter;
        _options = options.Value;
        _state = state;
        _trigger = trigger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SpoolReplayWorker starting, waiting 5s for initialization");
        await Task.Delay(5000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var lastBatchCount = 0;
            try
            {
                var batch = _spool.ReadBatch(_options.ReplayBatchSize);
                if (batch.Count > 0)
                {
                    lastBatchCount = await DrainChunkedAsync(batch, stoppingToken);

                    // If we drained a full read, yield briefly then loop - more data likely waiting
                    if (lastBatchCount >= _options.ReplayBatchSize)
                    {
                        await Task.Delay(250, stoppingToken);
                        continue;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in spool replay cycle");
            }

            // Re-read each cycle: the interval can be changed at runtime via the UI.
            var intervalSeconds = Math.Max(1, _exporter.FlushIntervalSeconds);
            _state.RecordCycle(intervalSeconds, lastBatchCount);

            // Sleep until the interval elapses OR a manual "flush now" is requested.
            try
            {
                await _trigger.WaitAsync(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }

        // Final replay attempt on shutdown
        try
        {
            var batch = _spool.ReadBatch(_options.ReplayBatchSize);
            if (batch.Count > 0)
                await DrainChunkedAsync(batch, CancellationToken.None);
        }
        catch (Exception ex) { _logger.LogError(ex, "Error during final spool replay"); }
    }

    /// <summary>
    /// Sends a replay batch in <see cref="SpoolOptions.SendChunkSize"/>-sized slices,
    /// committing each slice the moment it lands. Because the spool is FIFO and slices
    /// are taken in order, committing a slice removes exactly that prefix. On the first
    /// failed slice we stop and leave the remainder spooled for the next cycle — so a
    /// single oversized/slow send can't discard work that already succeeded, and a
    /// backlog (e.g. an error storm) drains steadily instead of wedging on the timeout.
    /// Returns the number of raw records committed.
    /// </summary>
    internal async Task<int> DrainChunkedAsync(List<NginxLogRecord> batch, CancellationToken ct)
    {
        var chunkSize = Math.Max(1, _options.SendChunkSize);
        var committed = 0;

        for (int offset = 0; offset < batch.Count; offset += chunkSize)
        {
            var chunk = batch.GetRange(offset, Math.Min(chunkSize, batch.Count - offset));
            if (!await _exporter.SendBatchAsync(chunk, ct))
                break;

            _spool.CommitBatch(chunk.Count);
            committed += chunk.Count;
        }

        return committed;
    }
}
