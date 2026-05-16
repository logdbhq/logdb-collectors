using com.logdb.docker.collector.Configuration;
using Microsoft.Extensions.Options;

namespace com.logdb.docker.collector.Services;

public class DockerMetricsWorker : BackgroundService
{
    private readonly DockerMetricsCollectorService _collector;
    private readonly ILogDbExporter _exporter;
    private readonly ILogger<DockerMetricsWorker> _logger;
    private readonly DockerMetricsOptions _options;
    private DateTime _lastErrorLogUtc;

    public DockerMetricsWorker(
        DockerMetricsCollectorService collector,
        ILogDbExporter exporter,
        ILogger<DockerMetricsWorker> logger,
        IOptions<DockerMetricsOptions> options)
    {
        _collector = collector;
        _exporter = exporter;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Docker metrics collection is disabled");
            return;
        }

        _logger.LogInformation("Docker metrics worker started ({Interval}s interval)", _options.CollectionIntervalSeconds);

        // Wait for initial discovery to complete
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var metrics = await _collector.CollectAsync(stoppingToken);

                if (metrics.Count > 0)
                {
                    await _exporter.SendMetricsBatchAsync(metrics, stoppingToken);
                    _logger.LogDebug("Exported {Count} container metrics", metrics.Count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if ((DateTime.UtcNow - _lastErrorLogUtc).TotalSeconds >= 60)
                {
                    _logger.LogError("Metrics collection cycle failed: {Msg}", ex.Message);
                    _lastErrorLogUtc = DateTime.UtcNow;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.CollectionIntervalSeconds), stoppingToken);
        }

        _logger.LogInformation("Docker metrics worker stopped");
    }
}
