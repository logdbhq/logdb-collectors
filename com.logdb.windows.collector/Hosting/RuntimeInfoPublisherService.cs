using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.shared.Services;

namespace com.logdb.windows.collector.Hosting;

public sealed class RuntimeInfoPublisherService : IHostedService
{
    private readonly CollectorRuntimeContext _runtimeContext;
    private readonly ILogger<RuntimeInfoPublisherService> _logger;

    public RuntimeInfoPublisherService(
        CollectorRuntimeContext runtimeContext,
        ILogger<RuntimeInfoPublisherService> logger)
    {
        _runtimeContext = runtimeContext;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var info = new CollectorRuntimeInfoDto
        {
            Mode = _runtimeContext.Mode,
            PipeName = _runtimeContext.ControlPipeName,
            ProcessId = _runtimeContext.ProcessId,
            StartedAtUtc = _runtimeContext.StartedAtUtc,
            ConfigPath = _runtimeContext.ConfigPath
        };

        await CollectorRuntimeInfoPersistence.SaveAsync(info, cancellationToken: cancellationToken);
        _logger.LogInformation(
            "Runtime info published at {Path} for mode {Mode}",
            CollectorRuntimeInfoPersistence.ResolvePath(_runtimeContext.Mode),
            _runtimeContext.Mode);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            CollectorRuntimeInfoPersistence.Remove(_runtimeContext.Mode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean runtime info file.");
        }

        return Task.CompletedTask;
    }
}
