using com.logdb.docker.collector.Configuration;
using Microsoft.Extensions.Options;

namespace com.logdb.docker.collector.Services;

public class DockerDiscoveryWorker : BackgroundService
{
    private readonly IDockerDiscoveryService _discovery;
    private readonly ILogger<DockerDiscoveryWorker> _logger;
    private readonly int _intervalSeconds;
    private DateTime _lastErrorLogUtc;

    public DockerDiscoveryWorker(
        IDockerDiscoveryService discovery,
        ILogger<DockerDiscoveryWorker> logger,
        IOptions<DockerDiscoveryOptions> options)
    {
        _discovery = discovery;
        _logger = logger;
        _intervalSeconds = options.Value.RefreshIntervalSeconds;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Discovery worker started ({Interval}s interval)", _intervalSeconds);

        // Initial discovery immediately
        try
        {
            await _discovery.RefreshAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError("Initial discovery failed: {Msg}", ex.Message);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), stoppingToken);

            try
            {
                await _discovery.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if ((DateTime.UtcNow - _lastErrorLogUtc).TotalSeconds >= 60)
                {
                    _logger.LogError("Discovery cycle failed: {Msg}", ex.Message);
                    _lastErrorLogUtc = DateTime.UtcNow;
                }
            }
        }

        _logger.LogInformation("Discovery worker stopped");
    }
}
