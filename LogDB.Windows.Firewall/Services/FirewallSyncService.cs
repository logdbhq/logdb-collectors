using System.Diagnostics;
using LogDB.Windows.Firewall.Models;

namespace LogDB.Windows.Firewall.Services;

public class FirewallSyncService : BackgroundService
{
    private readonly ILogger<FirewallSyncService> _logger;
    private readonly IConfiguration _configuration;
    private readonly BlocklistFetcher _blocklistFetcher;
    private readonly WindowsFirewallManager _firewallManager;
    private readonly FirewallStateTracker _stateTracker;
    private readonly CustomBlocklistClient? _customClient;
    private readonly SyncLogger _syncLogger;
    private readonly WhitelistService _whitelist;
    private readonly FirewallConfig _config;

    private static readonly Dictionary<string, string> SourceDisplayNames = new()
    {
        ["FireHOL_Level1"] = "FireHOL Level 1",
        ["FireHOL_Level2"] = "FireHOL Level 2",
        ["TorExitNodes"] = "Tor Exit Nodes",
        ["IPsum"] = "IPsum",
        ["Blocklist_de"] = "Blocklist.de",
        ["CINS_Army"] = "CINS Army",
        ["CustomBlocklist"] = "Custom Blocklist"
    };

    public FirewallSyncService(
        ILogger<FirewallSyncService> logger,
        IConfiguration configuration,
        BlocklistFetcher blocklistFetcher,
        WindowsFirewallManager firewallManager,
        FirewallStateTracker stateTracker,
        SyncLogger syncLogger,
        WhitelistService whitelist,
        CustomBlocklistClient? customClient = null)
    {
        _logger = logger;
        _configuration = configuration;
        _blocklistFetcher = blocklistFetcher;
        _firewallManager = firewallManager;
        _stateTracker = stateTracker;
        _syncLogger = syncLogger;
        _whitelist = whitelist;
        _customClient = customClient;
        _config = configuration.GetSection("Firewall").Get<FirewallConfig>() ?? new FirewallConfig();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("============================================================");
        _logger.LogInformation("  LogDB Windows Firewall Service - Starting");
        _logger.LogInformation("  Sync Interval: {Interval} minutes", _config.SyncIntervalMinutes);
        _logger.LogInformation("  Dry Run: {DryRun}", _config.DryRun);
        _logger.LogInformation("============================================================");

        // Load persisted state
        await _stateTracker.LoadAsync();
        _syncLogger.LoadPreviousSnapshots();

        // Verify admin access
        var hasAdmin = await _firewallManager.TestAdminAccessAsync();
        if (!hasAdmin)
        {
            _logger.LogCritical("No admin privileges! Cannot manage firewall rules. Run as Administrator or SYSTEM.");
            return;
        }

        // Initial delay
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSyncCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in sync cycle");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(_config.SyncIntervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("LogDB Windows Firewall Service - Stopped");
    }

    private async Task RunSyncCycleAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var totalIps = 0;
        var totalRules = 0;
        var enabledSourceIds = new HashSet<string>();

        _logger.LogInformation("--- Sync cycle started ---");
        await _syncLogger.LogSyncStartAsync();

        // Load whitelist (re-read every cycle so edits take effect without restart)
        var whitelisted = _whitelist.Load();
        if (whitelisted.Count > 0)
            _logger.LogInformation("  Whitelist active: {Count} entries", whitelisted.Count);

        // 1. Process public blocklists
        foreach (var (sourceId, config) in _config.PublicBlocklists)
        {
            if (!config.Enabled)
            {
                _logger.LogDebug("  Skipping disabled source: {SourceId}", sourceId);
                continue;
            }

            if (string.IsNullOrWhiteSpace(config.Url))
            {
                _logger.LogWarning("  Skipping {SourceId}: no URL configured", sourceId);
                continue;
            }

            enabledSourceIds.Add(sourceId);
            var displayName = SourceDisplayNames.GetValueOrDefault(sourceId, sourceId);

            try
            {
                var ips = await _blocklistFetcher.FetchAsync(sourceId, config.Url, config.MinScore, ct);
                var excluded = _whitelist.Apply(ips, whitelisted);
                if (excluded > 0)
                    _logger.LogInformation("  {Source}: excluded {Count} whitelisted IPs", displayName, excluded);
                var ruleCount = await _firewallManager.SyncSourceAsync(sourceId, displayName, ips);
                await _stateTracker.UpdateSourceStateAsync(sourceId, displayName, ips.Count, ruleCount, "active");
                await _syncLogger.LogSourceSyncAsync(sourceId, displayName, ips);

                totalIps += ips.Count;
                totalRules += ruleCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "  Failed to sync {SourceId}", sourceId);
                await _stateTracker.UpdateSourceStateAsync(sourceId, displayName, 0, 0, "error", ex.Message);
                await _syncLogger.LogSourceErrorAsync(displayName, ex.Message);
            }
        }

        // 2. Process custom blocklist (optional)
        if (_config.CustomBlocklist.Enabled && _customClient != null)
        {
            var sourceId = "CustomBlocklist";
            var displayName = SourceDisplayNames[sourceId];
            enabledSourceIds.Add(sourceId);

            try
            {
                var ips = await _customClient.FetchBlockedIpsAsync(ct);
                var excluded = _whitelist.Apply(ips, whitelisted);
                if (excluded > 0)
                    _logger.LogInformation("  {Source}: excluded {Count} whitelisted IPs", displayName, excluded);
                var ruleCount = await _firewallManager.SyncSourceAsync(sourceId, displayName, ips);
                await _stateTracker.UpdateSourceStateAsync(sourceId, displayName, ips.Count, ruleCount, "active");
                await _syncLogger.LogSourceSyncAsync(sourceId, displayName, ips);

                totalIps += ips.Count;
                totalRules += ruleCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "  Failed to sync custom blocklist");
                await _stateTracker.UpdateSourceStateAsync(sourceId, displayName, 0, 0, "error", ex.Message);
                await _syncLogger.LogSourceErrorAsync(displayName, ex.Message);
            }
        }

        // 3. Clean up rules for disabled/removed sources
        var allManagedRules = await _firewallManager.GetAllManagedRuleNamesAsync();
        foreach (var ruleName in allManagedRules)
        {
            var belongsToEnabledSource = false;
            foreach (var sourceId in enabledSourceIds)
            {
                var displayName = SourceDisplayNames.GetValueOrDefault(sourceId, sourceId);
                var baseRuleName = $"{_config.RulePrefix} - {displayName}";
                if (ruleName == baseRuleName || ruleName.StartsWith($"{baseRuleName} ("))
                {
                    belongsToEnabledSource = true;
                    break;
                }
            }

            if (!belongsToEnabledSource)
            {
                _logger.LogInformation("  Cleaning up orphaned rule: {RuleName}", ruleName);
                // Use SyncSourceAsync with empty set to remove, or direct removal
                // We'll do a simple PowerShell removal via the manager
                await _firewallManager.SyncSourceAsync(
                    "cleanup",
                    ruleName.Replace($"{_config.RulePrefix} - ", ""),
                    new HashSet<string>());
            }
        }

        sw.Stop();
        _logger.LogInformation("--- Sync cycle complete: {TotalIps} IPs across {TotalRules} rules in {Elapsed}ms ---",
            totalIps, totalRules, sw.ElapsedMilliseconds);
        await _syncLogger.LogSyncCompleteAsync(totalIps, totalRules, sw.ElapsedMilliseconds);
    }
}
