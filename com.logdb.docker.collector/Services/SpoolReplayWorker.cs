using com.logdb.docker.collector.Configuration;
using com.logdb.docker.collector.Models;
using Microsoft.Extensions.Options;

namespace com.logdb.docker.collector.Services;

public class SpoolReplayWorker : BackgroundService
{
    private readonly ISpoolStore _spool;
    private readonly ILogDbExporter _exporter;
    private readonly ILogger<SpoolReplayWorker> _logger;
    private readonly SpoolOptions _spoolOptions;
    private DateTime _lastErrorLogUtc;

    public SpoolReplayWorker(
        ISpoolStore spool,
        ILogDbExporter exporter,
        ILogger<SpoolReplayWorker> logger,
        IOptions<SpoolOptions> spoolOptions)
    {
        _spool = spool;
        _exporter = exporter;
        _logger = logger;
        _spoolOptions = spoolOptions.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Spool replay started ({Interval}s interval)", _spoolOptions.FlushIntervalSeconds);

        // Wait for initial discovery + first tail cycle
        await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = _spool.ReadBatch(_spoolOptions.ReplayBatchSize);

                if (batch.Count > 0)
                {
                    var committed = await DrainChunkedAsync(batch, stoppingToken);

                    // If we drained a full read, yield briefly then loop - more data likely waiting
                    if (committed >= _spoolOptions.ReplayBatchSize)
                    {
                        await Task.Delay(250, stoppingToken);
                        continue;
                    }
                    // Any uncommitted remainder stays in the spool - retried next cycle
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if ((DateTime.UtcNow - _lastErrorLogUtc).TotalSeconds >= 30)
                {
                    _logger.LogError("Spool replay failed: {Msg}", ex.Message);
                    _lastErrorLogUtc = DateTime.UtcNow;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(_spoolOptions.FlushIntervalSeconds), stoppingToken);
        }

        // Final replay attempt on shutdown
        try
        {
            var remaining = _spool.ReadBatch(_spoolOptions.ReplayBatchSize);
            if (remaining.Count > 0)
                await DrainChunkedAsync(remaining, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Final spool replay failed");
        }
    }

    /// <summary>
    /// Sends a replay batch in <see cref="SpoolOptions.SendChunkSize"/>-sized slices,
    /// committing each slice the moment it lands. The spool is FIFO and slices are taken
    /// in order, so committing a slice removes exactly that prefix. On the first failed
    /// slice we stop and leave the remainder spooled for the next cycle, so one slow/failed
    /// send can't discard work that already succeeded or wedge a backlog. Returns the
    /// number of records committed.
    /// </summary>
    internal async Task<int> DrainChunkedAsync(List<LogRecord> batch, CancellationToken ct)
    {
        var chunkSize = Math.Max(1, _spoolOptions.SendChunkSize);
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
