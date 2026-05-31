using com.logdb.docker.collector.Configuration;
using Microsoft.Extensions.Options;

namespace com.logdb.docker.collector.Services;

public class DockerMetricsWorker : BackgroundService
{
    private readonly DockerMetricsCollectorService _collector;
    private readonly ILogDbExporter _exporter;
    private readonly ILogger<DockerMetricsWorker> _logger;
    private readonly DockerMetricsOptions _options;
    private readonly MetricsSettingsService _settings;
    private DateTime _lastErrorLogUtc;

    public DockerMetricsWorker(
        DockerMetricsCollectorService collector,
        ILogDbExporter exporter,
        ILogger<DockerMetricsWorker> logger,
        IOptions<DockerMetricsOptions> options,
        MetricsSettingsService settings)
    {
        _collector = collector;
        _exporter = exporter;
        _logger = logger;
        _options = options.Value;
        _settings = settings;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Docker metrics collection is disabled");
            return;
        }

        _logger.LogInformation("Docker metrics worker started ({Interval}s interval)", _settings.IntervalSeconds);

        // Wait for initial discovery to complete
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        // Collect immediately, then re-collect once the configured interval has elapsed.
        // The interval is re-read each loop so changes made from the UI take effect promptly
        // (within one poll slice) without waiting out a full cycle of the old interval.
        DateTime? lastCollectionUtc = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalSeconds = _settings.IntervalSeconds;
            var due = lastCollectionUtc is null
                || (DateTime.UtcNow - lastCollectionUtc.Value).TotalSeconds >= intervalSeconds;

            if (due)
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

                lastCollectionUtc = DateTime.UtcNow;
            }

            // Poll in short slices so an interval change is picked up quickly, but never
            // sleep longer than the interval itself.
            var sliceSeconds = Math.Min(5, intervalSeconds);
            await Task.Delay(TimeSpan.FromSeconds(sliceSeconds), stoppingToken);
        }

        _logger.LogInformation("Docker metrics worker stopped");
    }
}
