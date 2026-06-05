using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using com.logdb.windows.collector.shared.Contracts;

namespace com.logdb.windows.collector.Services.Firewall;

/// <summary>
/// Owns the per-cycle work of getting the host firewall into the state the
/// configured public blocklists describe.
///
/// One Windows Firewall rule per (source, chunk-of-IPs). Chunked because the
/// PowerShell command line gets unwieldy at thousands of IPs, and the Defender
/// GUI gets cranky at very large RemoteAddress lists. The chunk size is
/// configurable per <see cref="FirewallConfigDto.MaxIpsPerRule"/>.
///
/// All rule writes flow through a temp-file pattern so the IP list never lives
/// on the command line at all — this is the only way the &gt;10k-entry feeds
/// (FireHOL, Blocklist.de) work reliably across Windows hosts with different
/// command-line length limits.
///
/// Replaces the per-IP <c>FirewallRuleApplier</c> shipped through 1.2.x, which
/// also fetched its IP list from a wrong endpoint (gRPC URL + REST path) and
/// would have created tens of thousands of single-IP rules anyway.
/// </summary>
public sealed class FirewallSyncEngine
{
    private readonly PublicBlocklistFetcher _fetcher;
    private readonly FirewallWhitelistService _whitelist;
    private readonly GuardBlocklistClient _guardClient;
    private readonly ILogger<FirewallSyncEngine> _logger;

    public FirewallSyncEngine(
        PublicBlocklistFetcher fetcher,
        FirewallWhitelistService whitelist,
        GuardBlocklistClient guardClient,
        ILogger<FirewallSyncEngine> logger)
    {
        _fetcher = fetcher;
        _whitelist = whitelist;
        _guardClient = guardClient;
        _logger = logger;
    }

    public bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public async Task<FirewallSyncSummary> SyncAsync(
        LogDbConfigDto logDbConfig,
        FirewallConfigDto config,
        CancellationToken cancellationToken = default)
    {
        if (!IsElevated())
            return FirewallSyncSummary.Failed("Elevation required to manage firewall rules.");

        var enabledFeeds = config.PublicBlocklists
            .Where(kvp => kvp.Value.Enabled && !string.IsNullOrWhiteSpace(kvp.Value.Url))
            .ToList();
        var customEnabled = config.CustomBlocklist.Enabled;
        if (enabledFeeds.Count == 0 && !customEnabled)
            return FirewallSyncSummary.Failed("No enabled blocklists are configured (public feeds and Guard blocklist are both off).");

        var whitelist = _whitelist.Load(config.WhitelistPath);
        var totalActiveRules = 0;
        var totalIps = 0;
        var perFeed = new List<FirewallFeedSyncSummary>();
        var activeDisplayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (feedId, feed) in enabledFeeds)
        {
            var ips = await _fetcher.FetchAsync(feedId, feed.Url, feed.MinScore, cancellationToken).ConfigureAwait(false);
            var whitelisted = FirewallWhitelistService.Apply(ips, whitelist);

            var displayName = string.IsNullOrWhiteSpace(feed.DisplayName) ? feedId : feed.DisplayName;
            var ruleCount = await SyncFeedAsync(config, displayName, ips, cancellationToken).ConfigureAwait(false);

            perFeed.Add(new FirewallFeedSyncSummary(feedId, displayName, ips.Count, ruleCount, whitelisted));
            activeDisplayNames.Add(displayName);
            totalActiveRules += ruleCount;
            totalIps += ips.Count;
        }

        if (customEnabled)
        {
            var customDisplayName = string.IsNullOrWhiteSpace(config.CustomBlocklist.DisplayName)
                ? "LogDB Guard"
                : config.CustomBlocklist.DisplayName;
            var guardIps = await _guardClient.FetchAsync(logDbConfig, config.CustomBlocklist, cancellationToken).ConfigureAwait(false);
            var whitelisted = FirewallWhitelistService.Apply(guardIps, whitelist);
            var ruleCount = await SyncFeedAsync(config, customDisplayName, guardIps, cancellationToken).ConfigureAwait(false);

            perFeed.Add(new FirewallFeedSyncSummary("custom_guard", customDisplayName, guardIps.Count, ruleCount, whitelisted));
            activeDisplayNames.Add(customDisplayName);
            totalActiveRules += ruleCount;
            totalIps += guardIps.Count;
        }

        // Drop any rule whose source isn't in the active set anymore (a feed
        // was disabled / removed, or the Guard mode was switched off) —
        // otherwise stale rules linger forever.
        await PruneOrphanedSourcesAsync(config, activeDisplayNames, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Firewall sync done: {Feeds} feeds, {Ips} unique IPs, {Rules} rules active",
            perFeed.Count, totalIps, totalActiveRules);

        return FirewallSyncSummary.Synced(totalIps, totalActiveRules, perFeed);
    }

    public async Task<(bool Success, string Message)> RemoveAllAsync(
        FirewallConfigDto config,
        CancellationToken cancellationToken = default)
    {
        if (!IsElevated())
            return (false, "Elevation required to remove firewall rules.");

        var prefix = string.IsNullOrWhiteSpace(config.RuleNamePrefix) ? "LogDB Firewall" : config.RuleNamePrefix;
        await RunPowerShellAsync(
            $"Get-NetFirewallRule | Where-Object {{ $_.DisplayName -like '{EscapePs(prefix)}*' }} | Remove-NetFirewallRule -ErrorAction SilentlyContinue",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Removed all firewall rules with prefix '{Prefix}'", prefix);
        return (true, $"Removed all firewall rules with prefix '{prefix}'.");
    }

    private async Task<int> SyncFeedAsync(
        FirewallConfigDto config,
        string displayName,
        HashSet<string> ips,
        CancellationToken cancellationToken)
    {
        var baseRuleName = $"{config.RuleNamePrefix} - {displayName}";

        if (ips.Count == 0)
        {
            var stale = await GetManagedRuleNamesAsync(baseRuleName, cancellationToken).ConfigureAwait(false);
            foreach (var ruleName in stale)
                await RemoveRuleAsync(ruleName, config.DryRun, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var chunkSize = config.MaxIpsPerRule > 0 ? config.MaxIpsPerRule : 5000;
        var ipList = ips.ToList();
        var chunks = new List<List<string>>();
        for (var i = 0; i < ipList.Count; i += chunkSize)
            chunks.Add(ipList.GetRange(i, Math.Min(chunkSize, ipList.Count - i)));

        for (var i = 0; i < chunks.Count; i++)
        {
            var ruleName = chunks.Count == 1 ? baseRuleName : $"{baseRuleName} ({i + 1}/{chunks.Count})";
            var existing = await GetRuleRemoteAddressesAsync(ruleName, cancellationToken).ConfigureAwait(false);
            var desired = chunks[i].ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (existing.SetEquals(desired))
                continue;

            if (existing.Count > 0)
                await UpdateRuleAsync(ruleName, chunks[i], config.DryRun, cancellationToken).ConfigureAwait(false);
            else
                await CreateRuleAsync(ruleName, chunks[i], config.Direction, config.DryRun, cancellationToken).ConfigureAwait(false);
        }

        // Trim leftover sub-rules from when the feed was larger (e.g. shrank
        // from 3 chunks to 2).
        var allExisting = await GetManagedRuleNamesAsync(baseRuleName, cancellationToken).ConfigureAwait(false);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (chunks.Count == 1)
        {
            keep.Add(baseRuleName);
        }
        else
        {
            for (var i = 0; i < chunks.Count; i++)
                keep.Add($"{baseRuleName} ({i + 1}/{chunks.Count})");
        }
        foreach (var existing in allExisting)
        {
            if (!keep.Contains(existing))
                await RemoveRuleAsync(existing, config.DryRun, cancellationToken).ConfigureAwait(false);
        }

        return chunks.Count;
    }

    private async Task PruneOrphanedSourcesAsync(
        FirewallConfigDto config,
        HashSet<string> enabledDisplayNames,
        CancellationToken cancellationToken)
    {
        var allManaged = await GetManagedRuleNamesAsync(config.RuleNamePrefix, cancellationToken).ConfigureAwait(false);
        var sourcePrefix = $"{config.RuleNamePrefix} - ";

        foreach (var rule in allManaged)
        {
            if (!rule.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var afterPrefix = rule[sourcePrefix.Length..];

            // Strip any " (n/m)" suffix to recover the source display name.
            var displayName = afterPrefix;
            var openParen = displayName.LastIndexOf(" (", StringComparison.Ordinal);
            if (openParen > 0 && displayName.EndsWith(")", StringComparison.Ordinal))
                displayName = displayName[..openParen];

            if (!enabledDisplayNames.Contains(displayName))
                await RemoveRuleAsync(rule, config.DryRun, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<List<string>> GetManagedRuleNamesAsync(string prefixOrBaseName, CancellationToken cancellationToken)
    {
        var output = await RunPowerShellWithOutputAsync(
            $"Get-NetFirewallRule | Where-Object {{ $_.DisplayName -like '{EscapePs(prefixOrBaseName)}*' }} | Select-Object -ExpandProperty DisplayName",
            cancellationToken).ConfigureAwait(false);

        return output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrEmpty(line))
            .ToList();
    }

    private async Task<HashSet<string>> GetRuleRemoteAddressesAsync(string ruleName, CancellationToken cancellationToken)
    {
        try
        {
            var command = $"$r = Get-NetFirewallRule -DisplayName '{EscapePs(ruleName)}' -ErrorAction SilentlyContinue; if ($r) {{ (Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $r).RemoteAddress }}";
            var output = await RunPowerShellWithOutputAsync(command, cancellationToken).ConfigureAwait(false);
            return output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrEmpty(line) && line != "Any" && line != "LocalSubnet")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task CreateRuleAsync(string ruleName, List<string> ips, string direction, bool dryRun, CancellationToken cancellationToken)
    {
        if (dryRun)
        {
            _logger.LogWarning("[DRY RUN] Would create rule '{RuleName}' with {Count} IPs", ruleName, ips.Count);
            return;
        }

        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(tempFile, ips, cancellationToken).ConfigureAwait(false);
            var command = $"$ips = Get-Content '{EscapePs(tempFile)}'; New-NetFirewallRule -DisplayName '{EscapePs(ruleName)}' -Direction {direction} -Action Block -RemoteAddress $ips -Enabled True -Profile Any -Description 'Managed by LogDB Windows Collector' | Out-Null";
            await RunPowerShellAsync(command, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Created rule '{RuleName}' with {Count} IPs", ruleName, ips.Count);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best-effort */ }
        }
    }

    private async Task UpdateRuleAsync(string ruleName, List<string> ips, bool dryRun, CancellationToken cancellationToken)
    {
        if (dryRun)
        {
            _logger.LogWarning("[DRY RUN] Would update rule '{RuleName}' with {Count} IPs", ruleName, ips.Count);
            return;
        }

        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(tempFile, ips, cancellationToken).ConfigureAwait(false);
            var command = $"$ips = Get-Content '{EscapePs(tempFile)}'; Set-NetFirewallRule -DisplayName '{EscapePs(ruleName)}' -RemoteAddress $ips";
            await RunPowerShellAsync(command, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Updated rule '{RuleName}' with {Count} IPs", ruleName, ips.Count);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best-effort */ }
        }
    }

    private async Task RemoveRuleAsync(string ruleName, bool dryRun, CancellationToken cancellationToken)
    {
        if (dryRun)
        {
            _logger.LogWarning("[DRY RUN] Would remove rule '{RuleName}'", ruleName);
            return;
        }

        await RunPowerShellAsync(
            $"Remove-NetFirewallRule -DisplayName '{EscapePs(ruleName)}' -ErrorAction SilentlyContinue",
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Removed rule '{RuleName}'", ruleName);
    }

    private async Task RunPowerShellAsync(string command, CancellationToken cancellationToken)
    {
        var (exitCode, _, stderr) = await RunPowerShellRawAsync(command, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            var error = stderr.Trim();
            if (!string.IsNullOrEmpty(error))
                throw new InvalidOperationException($"PowerShell command failed (exit {exitCode}): {error}");
        }
    }

    private async Task<string> RunPowerShellWithOutputAsync(string command, CancellationToken cancellationToken)
    {
        var (_, stdout, _) = await RunPowerShellRawAsync(command, cancellationToken).ConfigureAwait(false);
        return stdout;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunPowerShellRawAsync(
        string command,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string EscapePs(string value) => value.Replace("'", "''");
}

public sealed record FirewallSyncSummary(
    bool Success,
    string Message,
    int TotalActiveIps,
    int TotalActiveRules,
    IReadOnlyList<FirewallFeedSyncSummary> PerFeed)
{
    public static FirewallSyncSummary Failed(string message) =>
        new(false, message, 0, 0, Array.Empty<FirewallFeedSyncSummary>());

    public static FirewallSyncSummary Synced(int totalIps, int totalRules, IReadOnlyList<FirewallFeedSyncSummary> perFeed) =>
        new(true, $"Synced {perFeed.Count} feed(s), {totalIps} IPs across {totalRules} rule(s).",
            totalIps, totalRules, perFeed);
}

public sealed record FirewallFeedSyncSummary(
    string FeedId,
    string DisplayName,
    int IpsLoaded,
    int RuleChunks,
    int WhitelistedSkipped);
