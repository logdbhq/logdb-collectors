using com.logdb.nginx.collector.Configuration;
using Microsoft.Extensions.Options;

namespace com.logdb.nginx.collector.Services;

public class SpoolReplayWorker : BackgroundService
{
    private readonly ILogger<SpoolReplayWorker> _logger;
    private readonly ISpoolStore _spool;
    private readonly ILogDbExporter _exporter;
    private readonly SpoolOptions _options;

    public SpoolReplayWorker(ILogger<SpoolReplayWorker> logger, ISpoolStore spool, ILogDbExporter exporter, IOptions<SpoolOptions> options)
    {
        _logger = logger;
        _spool = spool;
        _exporter = exporter;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SpoolReplayWorker starting, waiting 5s for initialization");
        await Task.Delay(5000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = _spool.ReadBatch(_options.ReplayBatchSize);
                if (batch.Count > 0)
                {
                    var success = await _exporter.SendBatchAsync(batch, stoppingToken);
                    if (success)
                    {
                        _spool.CommitBatch(batch.Count);

                        // If we got a full batch, yield briefly then loop - more data likely waiting
                        if (batch.Count >= _options.ReplayBatchSize)
                        {
                            await Task.Delay(250, stoppingToken);
                            continue;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in spool replay cycle");
            }

            // Re-read each cycle: the interval can be changed at runtime via the UI.
            var intervalSeconds = Math.Max(1, _exporter.FlushIntervalSeconds);
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }

        // Final replay attempt on shutdown
        try
        {
            var batch = _spool.ReadBatch(_options.ReplayBatchSize);
            if (batch.Count > 0)
            {
                var success = await _exporter.SendBatchAsync(batch, CancellationToken.None);
                if (success) _spool.CommitBatch(batch.Count);
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error during final spool replay"); }
    }
}
