using com.logdb.nginx.collector.Configuration;
using Microsoft.Extensions.Options;

namespace com.logdb.nginx.collector.Services;

public class CheckpointFlushWorker : BackgroundService
{
    private readonly ILogger<CheckpointFlushWorker> _logger;
    private readonly ICheckpointStore _checkpointStore;
    private readonly CheckpointOptions _options;

    public CheckpointFlushWorker(ILogger<CheckpointFlushWorker> logger, ICheckpointStore checkpointStore, IOptions<CheckpointOptions> options)
    {
        _logger = logger;
        _checkpointStore = checkpointStore;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.FlushIntervalSeconds));
        _logger.LogInformation("CheckpointFlushWorker starting with interval {Interval}s", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(interval, stoppingToken);

            try
            {
                await _checkpointStore.FlushAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error flushing checkpoints");
            }
        }

        // Final flush on shutdown
        try { await _checkpointStore.FlushAsync(CancellationToken.None); }
        catch (Exception ex) { _logger.LogError(ex, "Error during final checkpoint flush"); }
    }
}
