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

    // Last status message actually written, for change-only logging.
    private string? _lastStatusLog;

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
                var summary = await _engine.SyncAsync(config.LogDB, config.Firewall, stoppingToken).ConfigureAwait(false);
                if (summary.IsIdle)
                {
                    // Module is on but nothing is configured to sync — benign idle,
                    // not an error. Log only when the state first changes (see
                    // LogStatusChange) so we don't pour an identical warning into the
                    // Windows event log every poll cycle.
                    _statusRegistry.MarkStopped(moduleName, "Idle");
                    LogStatusChange(LogLevel.Information, summary.Message);
                }
                else if (summary.Success)
                {
                    _statusRegistry.MarkRunning(moduleName);
                    _statusRegistry.MarkHeartbeat(moduleName);
                    LogStatusChange(LogLevel.Information, $"Firewall sync: {summary.Message}");
                }
                else
                {
                    _statusRegistry.MarkError(moduleName, summary.Message);
                    LogStatusChange(LogLevel.Warning, $"Firewall sync failed: {summary.Message}");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _statusRegistry.MarkError(moduleName, ex.Message);
                LogStatusChange(LogLevel.Error, $"Firewall module poll cycle threw: {ex.Message}", ex);
            }

            // Minimum 10s to avoid pounding upstream blocklist hosts if someone
            // mis-sets the interval. The 900s default is generous; FireHOL etc.
            // refresh hourly at most.
            var interval = Math.Max(10, _configMonitor.CurrentValue.Firewall.PollIntervalSeconds);
            await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Emit a log line only when the message differs from the previous cycle. A
    /// persistent condition (idle, elevation-required, an upstream feed down)
    /// then logs once on entry instead of every <c>PollInterval</c> — which is
    /// what flooded the Windows event log with tens of thousands of identical
    /// warnings (and, because the collector harvests its own Application channel,
    /// bloated the events database with re-ingested copies of them).
    /// </summary>
    private void LogStatusChange(LogLevel level, string message, Exception? ex = null)
    {
        if (message == _lastStatusLog)
            return;
        _lastStatusLog = message;
        if (ex != null)
            _logger.Log(level, ex, "{Message}", message);
        else
            _logger.Log(level, "{Message}", message);
    }
}
