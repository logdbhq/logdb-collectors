using com.logdb.docker.collector.Configuration;
using Microsoft.Extensions.Options;

namespace com.logdb.docker.collector.Services;

public class CheckpointFlushWorker : BackgroundService
{
    private readonly ICheckpointStore _store;
    private readonly ILogger<CheckpointFlushWorker> _logger;
    private readonly int _intervalSeconds;

    public CheckpointFlushWorker(
        ICheckpointStore store,
        ILogger<CheckpointFlushWorker> logger,
        IOptions<CheckpointOptions> options)
    {
        _store = store;
        _logger = logger;
        _intervalSeconds = options.Value.FlushIntervalSeconds;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Checkpoint flush worker started (interval: {Interval}s)", _intervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), stoppingToken);

            try
            {
                await _store.FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Checkpoint flush failed, will retry");
            }
        }

        // Final flush on shutdown
        try
        {
            await _store.FlushAsync(CancellationToken.None);
            _logger.LogInformation("Final checkpoint flush completed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Final checkpoint flush failed");
        }
    }
}
