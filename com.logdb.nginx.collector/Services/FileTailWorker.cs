using com.logdb.nginx.collector.Configuration;
using Microsoft.Extensions.Options;

namespace com.logdb.nginx.collector.Services;

public class FileTailWorker : BackgroundService
{
    private readonly ILogger<FileTailWorker> _logger;
    private readonly IFileTailService _tailService;
    private readonly TailOptions _options;

    public FileTailWorker(ILogger<FileTailWorker> logger, IFileTailService tailService,
        IOptions<TailOptions> options)
    {
        _logger = logger;
        _tailService = tailService;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FileTailWorker starting (active: {Active}s, idle: {Idle}s)",
            _options.ActiveIntervalSeconds, _options.IdleIntervalSeconds);
        await Task.Delay(3000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            bool hadData = false;
            try
            {
                hadData = await _tailService.TailAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in file tail cycle");
            }

            var interval = hadData ? _options.ActiveIntervalSeconds : _options.IdleIntervalSeconds;
            await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
        }
    }
}
