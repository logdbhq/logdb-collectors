using com.logdb.docker.collector.Configuration;
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
                    var success = await _exporter.SendBatchAsync(batch, stoppingToken);

                    if (success)
                    {
                        _spool.CommitBatch(batch.Count);

                        // If we got a full batch, yield briefly then loop - more data likely waiting
                        if (batch.Count >= _spoolOptions.ReplayBatchSize)
                        {
                            await Task.Delay(250, stoppingToken);
                            continue;
                        }
                    }
                    // If send failed, records remain in spool - will be retried next cycle
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
            {
                var success = await _exporter.SendBatchAsync(remaining, CancellationToken.None);
                if (success) _spool.CommitBatch(remaining.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Final spool replay failed");
        }
    }
}
