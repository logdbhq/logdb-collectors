using com.logdb.docker.collector.Configuration;
using Microsoft.Extensions.Options;

namespace com.logdb.docker.collector.Services;

public class FileTailWorker : BackgroundService
{
    private readonly IFileTailService _tailService;
    private readonly ILogger<FileTailWorker> _logger;
    private readonly TailOptions _options;
    private DateTime _lastErrorLogUtc;

    public FileTailWorker(IFileTailService tailService, ILogger<FileTailWorker> logger,
        IOptions<TailOptions> options)
    {
        _tailService = tailService;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("File tail worker started (active: {Active}s, idle: {Idle}s)",
            _options.ActiveIntervalSeconds, _options.IdleIntervalSeconds);

        // Wait briefly for initial discovery to populate
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            bool hadData = false;
            try
            {
                hadData = await _tailService.TailAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if ((DateTime.UtcNow - _lastErrorLogUtc).TotalSeconds >= 30)
                {
                    _logger.LogError("Tail cycle failed: {Msg}", ex.Message);
                    _lastErrorLogUtc = DateTime.UtcNow;
                }
            }

            var interval = hadData ? _options.ActiveIntervalSeconds : _options.IdleIntervalSeconds;
            await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
        }

        _logger.LogInformation("File tail worker stopped");
    }
}
