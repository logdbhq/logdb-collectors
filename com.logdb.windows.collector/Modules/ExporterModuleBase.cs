using System.Text.Json;
using com.logdb.windows.collector.Health;
using com.logdb.windows.collector.Services;
using com.logdb.windows.collector.shared.Contracts;
using Microsoft.Extensions.Options;

namespace com.logdb.windows.collector.Modules;

public abstract class ExporterModuleBase : BackgroundService
{
    private readonly string _moduleName;
    private readonly TimeSpan _idleDelay = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _runProbeDelay = TimeSpan.FromSeconds(5);

    private readonly IOptionsMonitor<CollectorConfigDto> _configMonitor;
    private readonly CollectorStatusRegistry _statusRegistry;
    private readonly IRuntimeEndpointStore _endpointStore;
    private readonly ILogger _logger;

    protected ExporterModuleBase(
        string moduleName,
        IOptionsMonitor<CollectorConfigDto> configMonitor,
        CollectorStatusRegistry statusRegistry,
        IRuntimeEndpointStore endpointStore,
        ILogger logger)
    {
        _moduleName = moduleName;
        _configMonitor = configMonitor;
        _statusRegistry = statusRegistry;
        _endpointStore = endpointStore;
        _logger = logger;
    }

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _statusRegistry.RegisterModule(_moduleName);

        while (!stoppingToken.IsCancellationRequested)
        {
            var currentConfig = _configMonitor.CurrentValue;
            var enabled = IsEnabled(currentConfig);
            _statusRegistry.SetEnabled(_moduleName, enabled);

            if (!enabled)
            {
                _statusRegistry.MarkStopped(_moduleName, "Disabled");
                await Task.Delay(_idleDelay, stoppingToken);
                continue;
            }

            try
            {
                // All modules read the same locked-in endpoint from the store.
                // Resolves discovery exactly once per service process (or once
                // per ApiKey / DiscoveryUrl change). No more independent
                // per-module resolves landing on different endpoints when
                // discovery is non-deterministic.
                var endpoint = await _endpointStore.GetEndpointAsync(stoppingToken);
                ApplyFlags(currentConfig);

                var fingerprint = ComputeFingerprint(currentConfig);
                using var moduleHost = BuildHost(currentConfig, endpoint);
                await moduleHost.StartAsync(stoppingToken);

                _statusRegistry.MarkRunning(_moduleName);
                _logger.LogInformation("{Module} started with endpoint {Endpoint}", _moduleName, endpoint);

                var waitTask = moduleHost.WaitForShutdownAsync(stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    var completed = await Task.WhenAny(waitTask, Task.Delay(_runProbeDelay, stoppingToken));
                    if (completed == waitTask)
                    {
                        // The same stoppingToken cancels BOTH tasks on service stop;
                        // when waitTask happens to win the WhenAny race this looked
                        // like a module crash and logged "<module> host stopped
                        // unexpectedly" on every normal shutdown/update. Only treat
                        // it as a fault when we are NOT being asked to stop.
                        if (stoppingToken.IsCancellationRequested)
                        {
                            break;
                        }

                        throw new InvalidOperationException($"{_moduleName} host stopped unexpectedly.");
                    }

                    _statusRegistry.MarkHeartbeat(_moduleName);
                    var latest = _configMonitor.CurrentValue;
                    if (!IsEnabled(latest) || ComputeFingerprint(latest) != fingerprint)
                    {
                        break;
                    }
                }

                await moduleHost.StopAsync(CancellationToken.None);
                _statusRegistry.MarkStopped(_moduleName, "Restarting");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _statusRegistry.MarkError(_moduleName, ex.Message);
                _logger.LogError(ex, "{Module} failed", _moduleName);
                await Task.Delay(_idleDelay, stoppingToken);
            }
        }

        _statusRegistry.MarkStopped(_moduleName, "Stopped");
    }

    private string ComputeFingerprint(CollectorConfigDto config)
    {
        var model = GetFingerprintModel(config);
        return JsonSerializer.Serialize(model);
    }

    protected virtual void ApplyFlags(CollectorConfigDto config)
    {
    }

    protected abstract bool IsEnabled(CollectorConfigDto config);
    protected abstract object GetFingerprintModel(CollectorConfigDto config);
    protected abstract IHost BuildHost(CollectorConfigDto config, string endpoint);
}
