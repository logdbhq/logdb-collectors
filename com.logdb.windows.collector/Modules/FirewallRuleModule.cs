using com.logdb.windows.collector.Health;
using com.logdb.windows.collector.Services.Firewall;
using com.logdb.windows.collector.shared.Contracts;
using Microsoft.Extensions.Options;

namespace com.logdb.windows.collector.Modules;

/// <summary>
/// Background worker that drives the host Windows Firewall toward the state
/// the configured public blocklists describe. Idempotent per poll, and a no-op
/// while <see cref="FirewallConfigDto.Enabled"/> is false (sleeps in a short
/// loop instead of sitting on the long PollInterval, so flipping the toggle
/// is responsive).
///
/// Before 1.3.0 this module fetched blocked IPs from a non-existent REST path
/// constructed by appending /api/guard/blocked-ips to the gRPC logger URL —
/// it was wired and shipped but had never produced a successful sync cycle.
/// 1.3.0 swapped in <see cref="FirewallSyncEngine"/>, which mirrors the
/// standalone LogDB.Windows.Firewall design (public threat feeds, whitelist
/// file, chunked rules) so the unified collector finally has a working
/// firewall capability.
/// </summary>
public sealed class FirewallRuleModule : BackgroundService
{
    private readonly IOptionsMonitor<CollectorConfigDto> _configMonitor;
    private readonly CollectorStatusRegistry _statusRegistry;
    private readonly FirewallSyncEngine _engine;
    private readonly ILogger<FirewallRuleModule> _logger;

    public FirewallRuleModule(
        IOptionsMonitor<CollectorConfigDto> configMonitor,
        CollectorStatusRegistry statusRegistry,
        FirewallSyncEngine engine,
        ILogger<FirewallRuleModule> logger)
    {
        _configMonitor = configMonitor;
        _statusRegistry = statusRegistry;
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const string moduleName = "Firewall";
        _statusRegistry.RegisterModule(moduleName);

        while (!stoppingToken.IsCancellationRequested)
        {
            var config = _configMonitor.CurrentValue;
            var enabled = config.Firewall.Enabled;
            _statusRegistry.SetEnabled(moduleName, enabled);

            if (!enabled)
            {
                _statusRegistry.MarkStopped(moduleName, "Disabled");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                var summary = await _engine.SyncAsync(config.Firewall, stoppingToken).ConfigureAwait(false);
                if (summary.Success)
                {
                    _statusRegistry.MarkRunning(moduleName);
                    _statusRegistry.MarkHeartbeat(moduleName);
                    _logger.LogInformation("Firewall sync: {Message}", summary.Message);
                }
                else
                {
                    _statusRegistry.MarkError(moduleName, summary.Message);
                    _logger.LogWarning("Firewall sync failed: {Message}", summary.Message);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _statusRegistry.MarkError(moduleName, ex.Message);
                _logger.LogError(ex, "Firewall module poll cycle threw");
            }

            // Minimum 10s to avoid pounding upstream blocklist hosts if someone
            // mis-sets the interval. The 900s default is generous; FireHOL etc.
            // refresh hourly at most.
            var interval = Math.Max(10, _configMonitor.CurrentValue.Firewall.PollIntervalSeconds);
            await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken).ConfigureAwait(false);
        }
    }
}
