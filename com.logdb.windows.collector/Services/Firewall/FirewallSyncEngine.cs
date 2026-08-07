using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
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
    /// <summary>Windows Firewall rule Group stamped on every rule we create —
    /// the reliable "this is ours" marker, independent of display names an
    /// operator might coincidentally reuse. Rules created before this shipped
    /// carry no group and are matched by display-name prefix (Legacy=true).</summary>
    public const string ManagedRuleGroup = "LogDB Collector";

    private static readonly JsonSerializerOptions RuleJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly PublicBlocklistFetcher _fetcher;
    private readonly FirewallWhitelistService _whitelist;
    private readonly GuardBlocklistClient _guardClient;
    private readonly FirewallRuleHistoryStore _history;
    private readonly FirewallBlockedIpIndex _blockedIndex;
    private readonly ILogger<FirewallSyncEngine> _logger;

    /// <summary>Feeds currently in the degraded state, so the history file gets
    /// one entry when a feed goes down and one when it comes back — not one per
    /// poll cycle for the whole outage. Engine is a singleton, so this survives
    /// across cycles.</summary>
    private readonly HashSet<string> _degradedReported = new(StringComparer.OrdinalIgnoreCase);

    public FirewallSyncEngine(
        PublicBlocklistFetcher fetcher,
        FirewallWhitelistService whitelist,
        GuardBlocklistClient guardClient,
        FirewallRuleHistoryStore history,
        FirewallBlockedIpIndex blockedIndex,
        ILogger<FirewallSyncEngine> logger)
    {
        _fetcher = fetcher;
        _whitelist = whitelist;
        _guardClient = guardClient;
        _history = history;
        _blockedIndex = blockedIndex;
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

        // Empty dictionary = feeds never configured (old config file) — fall
        // back to the stock feeds so enabling the module actually does
        // something. All-disabled is an explicit choice and is honored.
        var publicBlocklists = config.PublicBlocklists.Count > 0
            ? config.PublicBlocklists
            : FirewallDefaults.CreatePublicBlocklists();
        var enabledFeeds = publicBlocklists
            .Where(kvp => kvp.Value.Enabled && !string.IsNullOrWhiteSpace(kvp.Value.Url))
            .ToList();
        var customEnabled = config.CustomBlocklist.Enabled;
        if (enabledFeeds.Count == 0 && !customEnabled)
            // Not a failure — the module is on but the user simply hasn't enabled
            // any blocklist. A benign idle state, so it isn't surfaced as an error
            // or logged every poll cycle.
            return FirewallSyncSummary.Idle("No blocklists enabled — nothing to sync (public feeds and Guard blocklist are both off).");

        var whitelist = _whitelist.Load(config.WhitelistPath);
        var totalActiveRules = 0;
        var totalIps = 0;
        var totalChanges = 0;
        var perFeed = new List<FirewallFeedSyncSummary>();
        var activeDisplayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeSetsBySource = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // Feeds whose fetch failed this cycle. Their existing rules are held as
        // they are: an unreachable feed tells us nothing about what should stop
        // being blocked, and treating "I couldn't ask" as "block nothing" is how
        // a 30-second upstream outage used to unblock a whole feed. They stay in
        // activeDisplayNames so the orphan prune spares them, and out of
        // activeSetsBySource so the index reconcile spares them too.
        var degradedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Why / by whom / when, per Guard IP — the only source that supplies it.
        // Carried to the index so the Blocked IPs view can answer "why is this
        // blocked and since when" instead of just listing addresses.
        var provenance = new Dictionary<string, BlockedIpProvenance>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var (feedId, feed) in enabledFeeds)
            {
                var displayName = string.IsNullOrWhiteSpace(feed.DisplayName) ? feedId : feed.DisplayName;
                var fetch = await _fetcher.FetchAsync(feedId, feed.Url, feed.MinScore, cancellationToken).ConfigureAwait(false);

                if (!fetch.Success)
                {
                    var heldRules = await HoldFeedAsync(config, feedId, displayName, fetch.Error,
                        perFeed, activeDisplayNames, degradedSources, cancellationToken).ConfigureAwait(false);
                    totalActiveRules += heldRules;
                    continue;
                }

                var ips = fetch.Ips;
                MarkFeedRecovered(config, displayName, ips.Count);
                var whitelisted = FirewallWhitelistService.Apply(ips, whitelist);
                var (ruleCount, changes) = await SyncFeedAsync(config, displayName, ips, cancellationToken).ConfigureAwait(false);

                perFeed.Add(new FirewallFeedSyncSummary(feedId, displayName, ips.Count, ruleCount, whitelisted));
                activeDisplayNames.Add(displayName);
                activeSetsBySource[displayName] = ips.Select(NormalizeAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);
                totalActiveRules += ruleCount;
                totalIps += ips.Count;
                totalChanges += changes;
            }

            if (customEnabled)
            {
                var customDisplayName = string.IsNullOrWhiteSpace(config.CustomBlocklist.DisplayName)
                    ? "LogDB Guard"
                    : config.CustomBlocklist.DisplayName;
                var guardFetch = await _guardClient.FetchAsync(logDbConfig, config.CustomBlocklist, cancellationToken).ConfigureAwait(false);

                if (!guardFetch.Success)
                {
                    totalActiveRules += await HoldFeedAsync(config, "custom_guard", customDisplayName, guardFetch.Error,
                        perFeed, activeDisplayNames, degradedSources, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var guardEntries = guardFetch.Entries;
                    var guardIps = guardEntries.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    MarkFeedRecovered(config, customDisplayName, guardIps.Count);

                    foreach (var (guardIp, info) in guardEntries)
                    {
                        // Key on the normalized form: the index stores addresses
                        // normalized, so raw keys would never match on lookup.
                        provenance[NormalizeAddress(guardIp)] =
                            new BlockedIpProvenance(info.Reason, info.AddedBy, info.AddedAtUtc);
                    }

                    var whitelisted = FirewallWhitelistService.Apply(guardIps, whitelist);
                    var (ruleCount, changes) = await SyncFeedAsync(config, customDisplayName, guardIps, cancellationToken,
                        BuildGuardAnnotations(guardEntries)).ConfigureAwait(false);

                    perFeed.Add(new FirewallFeedSyncSummary("custom_guard", customDisplayName, guardIps.Count, ruleCount, whitelisted));
                    activeDisplayNames.Add(customDisplayName);
                    activeSetsBySource[customDisplayName] = guardIps.Select(NormalizeAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    totalActiveRules += ruleCount;
                    totalIps += guardIps.Count;
                    totalChanges += changes;
                }
            }

            // Drop any rule whose source isn't in the active set anymore (a feed
            // was disabled / removed, or the Guard mode was switched off) —
            // otherwise stale rules linger forever.
            totalChanges += await PruneOrphanedSourcesAsync(config, activeDisplayNames, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _history.Record(new FirewallRuleHistoryEntryDto
            {
                TimestampUtc = DateTime.UtcNow,
                Action = FirewallHistoryActions.SyncFailed,
                Success = false,
                DryRun = config.DryRun,
                Message = ex.Message
            });
            _logger.LogError(ex, "Firewall sync cycle failed");
            return FirewallSyncSummary.Failed($"Firewall sync failed: {ex.Message}");
        }

        // Only cycles that actually touched the firewall get a history entry —
        // a no-op poll every 15 minutes would flood the capped history file.
        if (totalChanges > 0)
        {
            _history.Record(new FirewallRuleHistoryEntryDto
            {
                TimestampUtc = DateTime.UtcNow,
                Action = FirewallHistoryActions.SyncCompleted,
                IpCount = totalIps,
                Success = true,
                DryRun = config.DryRun,
                Message = $"{perFeed.Count} feed(s), {totalIps} IPs, {totalActiveRules} rule(s) active, {totalChanges} rule change(s)"
            });
        }

        // Keep the per-IP block index aligned with what is now actually applied.
        // Dry-run cycles never touch the firewall, so they must not touch the
        // index either. Index errors are logged inside and never break a sync.
        if (!config.DryRun)
        {
            var (indexAdded, indexRemoved) = _blockedIndex.Reconcile(
                activeSetsBySource, degradedSources, DateTime.UtcNow, provenance);
            if (indexAdded > 0 || indexRemoved > 0)
            {
                _logger.LogInformation("Blocked-IP index: +{Added} / −{Removed}", indexAdded, indexRemoved);
            }
        }

        _logger.LogInformation(
            "Firewall sync done: {Feeds} feeds, {Ips} unique IPs, {Rules} rules active, {Degraded} feed(s) degraded",
            perFeed.Count, totalIps, totalActiveRules, degradedSources.Count);

        return FirewallSyncSummary.Synced(totalIps, totalActiveRules, perFeed);
    }

    /// <summary>
    /// Handles a feed whose fetch failed: leaves its rules exactly as they are,
    /// registers it as degraded, and reports how many of its rules are being
    /// held so the summary can say "still blocking, just not refreshed" rather
    /// than silently omitting the feed. Returns the held rule count.
    /// </summary>
    private async Task<int> HoldFeedAsync(
        FirewallConfigDto config,
        string feedId,
        string displayName,
        string error,
        List<FirewallFeedSyncSummary> perFeed,
        HashSet<string> activeDisplayNames,
        HashSet<string> degradedSources,
        CancellationToken cancellationToken)
    {
        var baseRuleName = $"{config.RuleNamePrefix} - {displayName}";
        var heldRules = (await GetManagedRuleNamesAsync(baseRuleName, cancellationToken).ConfigureAwait(false)).Count;

        // Spare it from the orphan prune and from the index reconcile.
        activeDisplayNames.Add(displayName);
        degradedSources.Add(displayName);

        perFeed.Add(new FirewallFeedSyncSummary(feedId, displayName, IpsLoaded: 0, heldRules, WhitelistedSkipped: 0, Error: error));

        _logger.LogWarning(
            "Feed '{DisplayName}' could not be refreshed ({Error}) — holding {Rules} existing rule(s); nothing was unblocked",
            displayName, error, heldRules);

        // Edge-triggered: one entry when the feed goes down, not one per poll.
        if (_degradedReported.Add(displayName))
        {
            _history.Record(new FirewallRuleHistoryEntryDto
            {
                TimestampUtc = DateTime.UtcNow,
                Action = FirewallHistoryActions.FeedUnavailable,
                RuleName = baseRuleName,
                Source = displayName,
                IpCount = 0,
                Success = false,
                DryRun = config.DryRun,
                Message = $"Fetch failed — {heldRules} existing rule(s) held, nothing unblocked. {error}"
            });
        }

        return heldRules;
    }

    /// <summary>Clears a feed's degraded flag after a successful fetch, and
    /// records the recovery when it had actually been reported as down.</summary>
    private void MarkFeedRecovered(FirewallConfigDto config, string displayName, int ipsLoaded)
    {
        if (!_degradedReported.Remove(displayName)) return;

        _logger.LogInformation("Feed '{DisplayName}' recovered — {Ips} IPs fetched", displayName, ipsLoaded);
        _history.Record(new FirewallRuleHistoryEntryDto
        {
            TimestampUtc = DateTime.UtcNow,
            Action = FirewallHistoryActions.FeedRecovered,
            Source = displayName,
            IpCount = ipsLoaded,
            Success = true,
            DryRun = config.DryRun,
            Message = $"Feed reachable again — {ipsLoaded} IPs fetched, rules refreshed."
        });
    }

    public async Task<(bool Success, string Message)> RemoveAllAsync(
        FirewallConfigDto config,
        CancellationToken cancellationToken = default)
    {
        if (!IsElevated())
            return (false, "Elevation required to remove firewall rules.");

        var prefix = string.IsNullOrWhiteSpace(config.RuleNamePrefix) ? "LogDB Firewall" : config.RuleNamePrefix;
        try
        {
            await RunPowerShellAsync(
                $"Get-NetFirewallRule | Where-Object {{ $_.DisplayName -like '{EscapePs(prefix)}*' }} | Remove-NetFirewallRule -ErrorAction SilentlyContinue",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _history.Record(new FirewallRuleHistoryEntryDto
            {
                TimestampUtc = DateTime.UtcNow,
                Action = FirewallHistoryActions.RemoveAll,
                Success = false,
                Message = ex.Message
            });
            return (false, $"Failed to remove firewall rules: {ex.Message}");
        }

        _history.Record(new FirewallRuleHistoryEntryDto
        {
            TimestampUtc = DateTime.UtcNow,
            Action = FirewallHistoryActions.RemoveAll,
            Success = true,
            Message = $"Removed all rules with prefix '{prefix}'"
        });
        _logger.LogInformation("Removed all firewall rules with prefix '{Prefix}'", prefix);
        return (true, $"Removed all firewall rules with prefix '{prefix}'.");
    }

    /// <summary>
    /// Reads back every LogDB rule live from the OS firewall — the ones this
    /// collector manages (group-tagged, plus legacy display-name-prefix
    /// matches) and, flagged <see cref="FirewallRuleInfoDto.Unmanaged"/>, any
    /// other "LogDB*" rule present on the host.
    ///
    /// The unmanaged half matters because the desktop app's firewall-export
    /// script writes rules like "LogDB.Guard - Blocked IPs" that block traffic
    /// on this same host but are refreshed only when someone re-runs the
    /// script. Listing only our own rules made those invisible here, so an
    /// operator comparing this list against the app's blocklist would conclude
    /// sync was broken when in fact two independent mechanisms were in play.
    /// </summary>
    public async Task<IReadOnlyList<FirewallRuleInfoDto>> ListRulesAsync(
        FirewallConfigDto config,
        CancellationToken cancellationToken = default)
    {
        var prefix = string.IsNullOrWhiteSpace(config.RuleNamePrefix) ? "LogDB Firewall" : config.RuleNamePrefix;
        var command =
            $"$rules = Get-NetFirewallRule | Where-Object {{ $_.Group -eq '{ManagedRuleGroup}' -or $_.DisplayName -like '{EscapePs(prefix)}*' -or $_.DisplayName -like 'LogDB*' }}; " +
            "$out = foreach ($r in $rules) { " +
            "$af = Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $r; " +
            "$ips = @($af.RemoteAddress | Where-Object { $_ -and $_ -ne 'Any' -and $_ -ne 'LocalSubnet' }); " +
            $"$managed = ($r.Group -eq '{ManagedRuleGroup}') -or ($r.DisplayName -like '{EscapePs(prefix)}*'); " +
            $"[pscustomobject]@{{ id = $r.Name; displayName = $r.DisplayName; direction = [string]$r.Direction; enabled = ([string]$r.Enabled -eq 'True'); ipCount = $ips.Count; legacy = ($managed -and $r.Group -ne '{ManagedRuleGroup}'); unmanaged = (-not $managed) }} }}; " +
            "ConvertTo-Json -InputObject @($out) -Compress";

        var output = await RunPowerShellWithOutputAsync(command, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output)) return Array.Empty<FirewallRuleInfoDto>();

        List<FirewallRuleInfoDto>? rules;
        try
        {
            rules = JsonSerializer.Deserialize<List<FirewallRuleInfoDto>>(output.Trim(), RuleJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse firewall rule listing");
            return Array.Empty<FirewallRuleInfoDto>();
        }

        if (rules == null) return Array.Empty<FirewallRuleInfoDto>();
        foreach (var rule in rules)
        {
            // An unmanaged rule's display name follows whatever convention its
            // writer chose, so the feed-name extraction would invent a bogus
            // source. Report the rule name itself instead.
            rule.Source = rule.Unmanaged
                ? rule.DisplayName
                : ExtractSourceFromDisplayName(rule.DisplayName, config.RuleNamePrefix);
        }
        return rules;
    }

    /// <summary>
    /// Deletes one managed rule by its unique Name. For Guard-sourced rules
    /// (and <paramref name="request"/>.RemoveFromBackend), the rule's IPs are
    /// first removed from the Guard backend so the next sync cycle doesn't
    /// immediately re-block them; public-feed rules will reappear on the next
    /// sync as long as the feed still lists their IPs — the returned message
    /// says which case applies.
    /// </summary>
    public async Task<(bool Success, string Message)> DeleteRuleAsync(
        LogDbConfigDto logDbConfig,
        FirewallConfigDto config,
        DeleteFirewallRuleRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (!IsElevated())
            return (false, "Elevation required to delete firewall rules.");
        if (string.IsNullOrWhiteSpace(request.RuleId))
            return (false, "Rule id is required.");

        var lookup =
            $"$r = Get-NetFirewallRule -Name '{EscapePs(request.RuleId)}' -ErrorAction SilentlyContinue; " +
            "if ($r) { $af = Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $r; " +
            "ConvertTo-Json -InputObject ([pscustomobject]@{ displayName = $r.DisplayName; group = [string]$r.Group; ips = @($af.RemoteAddress | Where-Object { $_ -and $_ -ne 'Any' -and $_ -ne 'LocalSubnet' }) }) -Compress }";
        var output = await RunPowerShellWithOutputAsync(lookup, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output))
            return (false, $"No firewall rule found with id '{request.RuleId}'.");

        string displayName;
        string group;
        List<string> ips;
        try
        {
            var details = JsonSerializer.Deserialize<RuleDeletionDetails>(output.Trim(), RuleJsonOptions);
            displayName = details?.DisplayName ?? request.RuleId;
            group = details?.Group ?? string.Empty;
            // Normalized so backend removal matches Guard's stored form and the
            // history entry shows CIDR rather than dotted netmasks.
            ips = (details?.Ips ?? new List<string>()).Select(NormalizeAddress).ToList();
        }
        catch (JsonException)
        {
            displayName = request.RuleId;
            group = string.Empty;
            ips = new List<string>();
        }

        // ListRulesAsync now also reports LogDB rules written by something else
        // (the desktop app's export script). Those are visible so the operator
        // knows they exist — but deleting them here would silently undo another
        // tool's enforcement, and the next sync would not re-create them.
        var rulePrefix = string.IsNullOrWhiteSpace(config.RuleNamePrefix) ? "LogDB Firewall" : config.RuleNamePrefix;
        var isManaged = string.Equals(group, ManagedRuleGroup, StringComparison.OrdinalIgnoreCase)
            || displayName.StartsWith(rulePrefix, StringComparison.OrdinalIgnoreCase);
        if (!isManaged)
            return (false,
                $"'{displayName}' is not managed by this collector — it was created outside it " +
                "(most likely by the LogDB desktop app's firewall-export script). Remove it with " +
                $"Remove-NetFirewallRule -Name '{request.RuleId}' if that is what you want.");

        var source = ExtractSourceFromDisplayName(displayName, config.RuleNamePrefix);
        var guardDisplayName = string.IsNullOrWhiteSpace(config.CustomBlocklist.DisplayName)
            ? "LogDB Guard"
            : config.CustomBlocklist.DisplayName;
        var isGuardRule = string.Equals(source, guardDisplayName, StringComparison.OrdinalIgnoreCase);

        // Capture the operator comments BEFORE the backend removal — deleting a
        // Guard row destroys its reason with no tombstone, so the history entry
        // written below is the only surviving record of why each IP was blocked.
        IReadOnlyDictionary<string, string>? annotations = null;
        if (isGuardRule)
        {
            var guardFetch = await _guardClient
                .FetchAsync(logDbConfig, config.CustomBlocklist, cancellationToken)
                .ConfigureAwait(false);
            if (guardFetch.Entries.Count > 0) annotations = BuildGuardAnnotations(guardFetch.Entries);
        }

        string? backendNote = null;
        if (isGuardRule && request.RemoveFromBackend)
        {
            var (backendOk, backendMessage) = await _guardClient
                .RemoveBlockedIpsAsync(logDbConfig, config.CustomBlocklist, ips, cancellationToken)
                .ConfigureAwait(false);
            backendNote = backendOk
                ? backendMessage
                : $"WARNING: backend removal failed — the next sync may re-apply this rule. {backendMessage}";
        }

        try
        {
            await RunPowerShellAsync(
                $"Remove-NetFirewallRule -Name '{EscapePs(request.RuleId)}'",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordRuleChange(FirewallHistoryActions.RuleRemoved, displayName, source, ips.Count, success: false,
                message: $"Operator delete failed: {ex.Message}");
            return (false, $"Failed to delete rule '{displayName}': {ex.Message}");
        }

        var note = backendNote
            ?? (isGuardRule
                ? "Backend removal was skipped."
                : "Public-feed rule: it will be re-created on the next sync while the feed still lists these IPs.");
        RecordRuleChange(FirewallHistoryActions.RuleRemoved, displayName, source, ips.Count, success: true,
            message: $"Deleted by operator. {note}", removed: ips, annotations: annotations);
        _logger.LogInformation("Operator deleted firewall rule '{DisplayName}' (id {RuleId}). {Note}", displayName, request.RuleId, note);

        return (true, $"Deleted rule '{displayName}'. {note}");
    }

    /// <summary>
    /// Reads one managed rule's live RemoteAddress list from the OS firewall
    /// by its unique Name — the on-demand answer to "which IPs does this rule
    /// block right now?", with nothing persisted.
    /// </summary>
    public async Task<(bool Success, string Message, FirewallRuleIpsDto? Rule)> GetRuleIpsAsync(
        string ruleId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
            return (false, "Rule id is required.", null);

        var lookup =
            $"$r = Get-NetFirewallRule -Name '{EscapePs(ruleId)}' -ErrorAction SilentlyContinue; " +
            "if ($r) { $af = Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $r; " +
            "ConvertTo-Json -InputObject ([pscustomobject]@{ displayName = $r.DisplayName; ips = @($af.RemoteAddress | Where-Object { $_ -and $_ -ne 'Any' -and $_ -ne 'LocalSubnet' }) }) -Compress }";
        var output = await RunPowerShellWithOutputAsync(lookup, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output))
            return (false, $"No firewall rule found with id '{ruleId}'.", null);

        try
        {
            var details = JsonSerializer.Deserialize<RuleDeletionDetails>(output.Trim(), RuleJsonOptions);
            return (true, string.Empty, new FirewallRuleIpsDto
            {
                Id = ruleId,
                DisplayName = details?.DisplayName ?? ruleId,
                Ips = (details?.Ips ?? new List<string>()).Select(NormalizeAddress).ToList()
            });
        }
        catch (JsonException ex)
        {
            return (false, $"Failed to parse rule address list: {ex.Message}", null);
        }
    }

    private sealed class RuleDeletionDetails
    {
        public string? DisplayName { get; set; }
        public string? Group { get; set; }
        public List<string>? Ips { get; set; }
    }

    /// <summary>Deterministic unique rule Name: same display name always maps
    /// to the same id, so re-syncs stay idempotent and the UI has a stable
    /// handle for deletion.</summary>
    public static string UniqueRuleId(string ruleDisplayName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(ruleDisplayName));
        return "LogDB-FW-" + Convert.ToHexString(hash, 0, 6);
    }

    /// <summary>
    /// Canonicalizes an address for comparison and display: Windows Firewall
    /// returns rule addresses in dotted-netmask form ("2.57.122.188/255.255.255.252")
    /// while feeds use CIDR ("2.57.122.188/30"). Without normalization the
    /// existing-vs-desired set comparison never matched for CIDR entries, so
    /// every sync cycle rewrote every rule and logged the whole feed as
    /// +N/−N churn. Full-host masks (/32, /128) collapse to the bare IP.
    /// Unparseable or non-contiguous masks are returned unchanged.
    /// </summary>
    public static string NormalizeAddress(string value)
    {
        var v = value.Trim();
        var slash = v.IndexOf('/');
        if (slash < 0) return v;

        var ip = v[..slash];
        var suffix = v[(slash + 1)..];
        if (!System.Net.IPAddress.TryParse(ip, out var address)) return v;
        var maxPrefix = address.GetAddressBytes().Length * 8;

        if (int.TryParse(suffix, out var numericPrefix))
        {
            if (numericPrefix < 0 || numericPrefix > maxPrefix) return v;
            return numericPrefix == maxPrefix ? ip : $"{ip}/{numericPrefix}";
        }

        if (!System.Net.IPAddress.TryParse(suffix, out var mask)) return v;
        var bytes = mask.GetAddressBytes();
        var prefix = 0;
        var zeroSeen = false;
        foreach (var b in bytes)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                if ((b >> bit & 1) == 1)
                {
                    if (zeroSeen) return v;   // non-contiguous mask — leave as-is
                    prefix++;
                }
                else
                {
                    zeroSeen = true;
                }
            }
        }

        return prefix == bytes.Length * 8 ? ip : $"{ip}/{prefix}";
    }

    /// <summary>Recovers the feed display name from a rule display name by
    /// stripping the configured prefix and any " (n/m)" chunk suffix.</summary>
    public static string ExtractSourceFromDisplayName(string ruleDisplayName, string ruleNamePrefix)
    {
        var prefix = string.IsNullOrWhiteSpace(ruleNamePrefix) ? "LogDB Firewall" : ruleNamePrefix;
        var sourcePrefix = $"{prefix} - ";
        var source = ruleDisplayName.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)
            ? ruleDisplayName[sourcePrefix.Length..]
            : ruleDisplayName;

        var openParen = source.LastIndexOf(" (", StringComparison.Ordinal);
        if (openParen > 0 && source.EndsWith(")", StringComparison.Ordinal))
            source = source[..openParen];
        return source;
    }

    private async Task<(int RuleCount, int Changes)> SyncFeedAsync(
        FirewallConfigDto config,
        string displayName,
        HashSet<string> ips,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? annotations = null)
    {
        var baseRuleName = $"{config.RuleNamePrefix} - {displayName}";
        var changes = 0;

        if (ips.Count == 0)
        {
            var stale = await GetManagedRuleNamesAsync(baseRuleName, cancellationToken).ConfigureAwait(false);
            foreach (var ruleName in stale)
            {
                await RemoveRuleAsync(ruleName, displayName, config.DryRun, cancellationToken, annotations).ConfigureAwait(false);
                changes++;
            }
            return (0, changes);
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
            var desired = chunks[i].Select(NormalizeAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (existing.SetEquals(desired))
                continue;

            if (existing.Count > 0)
            {
                var added = desired.Where(ip => !existing.Contains(ip)).ToList();
                var removed = existing.Where(ip => !desired.Contains(ip)).ToList();
                await UpdateRuleAsync(ruleName, displayName, chunks[i], added, removed, config.DryRun, cancellationToken, annotations).ConfigureAwait(false);
            }
            else
            {
                await CreateRuleAsync(ruleName, displayName, chunks[i], config.Direction, config.DryRun, cancellationToken, annotations).ConfigureAwait(false);
            }
            changes++;
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
            {
                await RemoveRuleAsync(existing, displayName, config.DryRun, cancellationToken, annotations).ConfigureAwait(false);
                changes++;
            }
        }

        return (chunks.Count, changes);
    }

    private async Task<int> PruneOrphanedSourcesAsync(
        FirewallConfigDto config,
        HashSet<string> enabledDisplayNames,
        CancellationToken cancellationToken)
    {
        var allManaged = await GetManagedRuleNamesAsync(config.RuleNamePrefix, cancellationToken).ConfigureAwait(false);
        var sourcePrefix = $"{config.RuleNamePrefix} - ";
        var changes = 0;

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
            {
                await RemoveRuleAsync(rule, displayName, config.DryRun, cancellationToken).ConfigureAwait(false);
                changes++;
            }
        }

        return changes;
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
                .Select(NormalizeAddress)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task CreateRuleAsync(string ruleName, string source, List<string> ips, string direction, bool dryRun, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? annotations = null)
    {
        if (dryRun)
        {
            _logger.LogWarning("[DRY RUN] Would create rule '{RuleName}' with {Count} IPs", ruleName, ips.Count);
            RecordRuleChange(FirewallHistoryActions.RuleCreated, ruleName, source, ips.Count, success: true, dryRun: true,
                added: ips, annotations: annotations);
            return;
        }

        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(tempFile, ips, cancellationToken).ConfigureAwait(false);
            var command = $"$ips = Get-Content '{EscapePs(tempFile)}'; New-NetFirewallRule -Name '{EscapePs(UniqueRuleId(ruleName))}' -DisplayName '{EscapePs(ruleName)}' -Group '{ManagedRuleGroup}' -Direction {direction} -Action Block -RemoteAddress $ips -Enabled True -Profile Any -Description 'Managed by LogDB Windows Collector' | Out-Null";
            await RunPowerShellAsync(command, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Created rule '{RuleName}' with {Count} IPs", ruleName, ips.Count);
            RecordRuleChange(FirewallHistoryActions.RuleCreated, ruleName, source, ips.Count, success: true,
                added: ips, annotations: annotations);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordRuleChange(FirewallHistoryActions.RuleCreated, ruleName, source, ips.Count, success: false, message: ex.Message);
            throw;
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best-effort */ }
        }
    }

    private async Task UpdateRuleAsync(string ruleName, string source, List<string> ips, List<string> added, List<string> removed, bool dryRun, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? annotations = null)
    {
        if (dryRun)
        {
            _logger.LogWarning("[DRY RUN] Would update rule '{RuleName}' with {Count} IPs", ruleName, ips.Count);
            RecordRuleChange(FirewallHistoryActions.RuleUpdated, ruleName, source, ips.Count, success: true, dryRun: true,
                added: added, removed: removed, annotations: annotations);
            return;
        }

        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(tempFile, ips, cancellationToken).ConfigureAwait(false);
            var command = $"$ips = Get-Content '{EscapePs(tempFile)}'; Set-NetFirewallRule -DisplayName '{EscapePs(ruleName)}' -RemoteAddress $ips";
            await RunPowerShellAsync(command, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Updated rule '{RuleName}' with {Count} IPs (+{Added}/−{Removed})", ruleName, ips.Count, added.Count, removed.Count);
            RecordRuleChange(FirewallHistoryActions.RuleUpdated, ruleName, source, ips.Count, success: true,
                added: added, removed: removed, annotations: annotations);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordRuleChange(FirewallHistoryActions.RuleUpdated, ruleName, source, ips.Count, success: false, message: ex.Message);
            throw;
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best-effort */ }
        }
    }

    private async Task RemoveRuleAsync(string ruleName, string source, bool dryRun, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? annotations = null)
    {
        // Read the rule's IPs before removal so the history entry can say what
        // stopped being blocked; best-effort — an empty set just means no delta.
        var removedIps = await GetRuleRemoteAddressesAsync(ruleName, cancellationToken).ConfigureAwait(false);

        if (dryRun)
        {
            _logger.LogWarning("[DRY RUN] Would remove rule '{RuleName}'", ruleName);
            RecordRuleChange(FirewallHistoryActions.RuleRemoved, ruleName, source, ipCount: 0, success: true, dryRun: true,
                removed: removedIps.ToList(), annotations: annotations);
            return;
        }

        try
        {
            await RunPowerShellAsync(
                $"Remove-NetFirewallRule -DisplayName '{EscapePs(ruleName)}' -ErrorAction SilentlyContinue",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordRuleChange(FirewallHistoryActions.RuleRemoved, ruleName, source, ipCount: 0, success: false, message: ex.Message);
            throw;
        }

        _logger.LogInformation("Removed rule '{RuleName}'", ruleName);
        RecordRuleChange(FirewallHistoryActions.RuleRemoved, ruleName, source, ipCount: 0, success: true,
            removed: removedIps.ToList(), annotations: annotations);
    }

    /// <summary>History entries store a capped sample of the changed IPs, plus
    /// exact counts — the full 5000-IP chunk would bloat the JSONL file.</summary>
    private const int MaxIpSampleSize = 50;

    private void RecordRuleChange(
        string action,
        string ruleName,
        string source,
        int ipCount,
        bool success,
        bool dryRun = false,
        string message = "",
        List<string>? added = null,
        List<string>? removed = null,
        IReadOnlyDictionary<string, string>? annotations = null)
    {
        _history.Record(new FirewallRuleHistoryEntryDto
        {
            TimestampUtc = DateTime.UtcNow,
            Action = action,
            RuleName = ruleName,
            Source = source,
            IpCount = ipCount,
            Success = success,
            DryRun = dryRun,
            Message = message,
            AddedCount = added?.Count ?? 0,
            RemovedCount = removed?.Count ?? 0,
            AddedIps = AnnotatedSample(added, annotations),
            RemovedIps = AnnotatedSample(removed, annotations)
        });
    }

    /// <summary>Caps the sample and appends each IP's operator comment when one
    /// is known (Guard-sourced IPs): "1.2.3.4 — SSH brute force (vladimir)".</summary>
    private static List<string> AnnotatedSample(List<string>? ips, IReadOnlyDictionary<string, string>? annotations)
    {
        if (ips == null) return new List<string>();
        return ips
            .Take(MaxIpSampleSize)
            .Select(ip => annotations != null && annotations.TryGetValue(ip, out var note) && note.Length > 0
                ? $"{ip} — {note}"
                : ip)
            .ToList();
    }

    /// <summary>Turns Guard entries into per-IP display notes: the operator's
    /// free-text reason plus who added it. Reason is unsanitized and unbounded
    /// server-side, so it is whitespace-collapsed and capped here — display
    /// text only, never parsed.</summary>
    private static Dictionary<string, string> BuildGuardAnnotations(
        Dictionary<string, GuardBlocklistClient.GuardBlockedIpInfo> entries)
    {
        var annotations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ip, info) in entries)
        {
            var reason = CompactText(info.Reason, 120);
            var addedBy = CompactText(info.AddedBy, 40);
            var note = (reason, addedBy) switch
            {
                ("", "") => string.Empty,
                ("", _) => $"added by {addedBy}",
                (_, "") => reason,
                _ => $"{reason} ({addedBy})"
            };
            if (note.Length > 0) annotations[ip] = note;
        }
        return annotations;
    }

    private static string CompactText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var compact = System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim();
        return compact.Length <= maxLength ? compact : compact[..maxLength] + "…";
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
    /// <summary>
    /// True when the cycle did no work because nothing is configured to sync
    /// (no blocklists enabled). Benign idle state — <see cref="Success"/> is true
    /// so it is never treated as an error, and callers must not log it every cycle.
    /// </summary>
    public bool IsIdle { get; init; }

    /// <summary>Feeds that could not be fetched this cycle. Their rules are
    /// still applied — just not refreshed — so the cycle is neither a success
    /// nor a failure, and callers must report it as neither.</summary>
    public IReadOnlyList<FirewallFeedSyncSummary> DegradedFeeds =>
        PerFeed.Where(f => f.Degraded).ToList();

    public bool IsDegraded => PerFeed.Any(f => f.Degraded);

    public static FirewallSyncSummary Failed(string message) =>
        new(false, message, 0, 0, Array.Empty<FirewallFeedSyncSummary>());

    public static FirewallSyncSummary Idle(string message) =>
        new(true, message, 0, 0, Array.Empty<FirewallFeedSyncSummary>()) { IsIdle = true };

    public static FirewallSyncSummary Synced(int totalIps, int totalRules, IReadOnlyList<FirewallFeedSyncSummary> perFeed)
    {
        var degraded = perFeed.Where(f => f.Degraded).ToList();
        var message = degraded.Count == 0
            ? $"Synced {perFeed.Count} feed(s), {totalIps} IPs across {totalRules} rule(s)."
            // Name the feeds and say explicitly that nothing was unblocked — the
            // whole point is that this state is not mistaken for a clean sync.
            : $"Synced {perFeed.Count - degraded.Count} of {perFeed.Count} feed(s), {totalIps} IPs across {totalRules} rule(s). "
              + $"Could not refresh: {string.Join(", ", degraded.Select(f => $"{f.DisplayName} ({f.Error})"))}. "
              + "Existing rules for those feeds are still applied — nothing was unblocked.";

        return new FirewallSyncSummary(true, message, totalIps, totalRules, perFeed);
    }
}

/// <param name="RuleChunks">Rules active for this feed. For a degraded feed
/// these are rules held unchanged from the last good cycle, not rules just
/// written.</param>
/// <param name="Error">Empty when the feed synced normally; the fetch failure
/// reason otherwise.</param>
public sealed record FirewallFeedSyncSummary(
    string FeedId,
    string DisplayName,
    int IpsLoaded,
    int RuleChunks,
    int WhitelistedSkipped,
    string Error = "")
{
    public bool Degraded => !string.IsNullOrEmpty(Error);
}
