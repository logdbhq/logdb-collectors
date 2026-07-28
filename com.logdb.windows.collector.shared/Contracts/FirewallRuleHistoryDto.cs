namespace com.logdb.windows.collector.shared.Contracts;

/// <summary>
/// One recorded change to the host Windows Firewall made by the collector —
/// a rule created, updated, or removed, or a whole sync/remove-all cycle.
/// Persisted by the service to firewall-history.jsonl and served to the UI
/// via ControlCommands.GetFirewallHistory, so the history survives service
/// restarts (unlike the in-memory diagnostics ring the UI used to scrape).
/// </summary>
public sealed class FirewallRuleHistoryEntryDto
{
    public DateTime TimestampUtc { get; set; }

    /// <summary>One of <see cref="FirewallHistoryActions"/>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Firewall rule display name, empty for cycle-level entries.</summary>
    public string RuleName { get; set; } = string.Empty;

    /// <summary>Feed display name the rule belongs to (e.g. "FireHOL Level 1",
    /// "LogDB Guard"), empty when unknown (prune/remove-all).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>IPs/CIDRs in the rule after the change; 0 when not applicable.</summary>
    public int IpCount { get; set; }

    public bool Success { get; set; }

    /// <summary>True when the change was only logged (firewall untouched).</summary>
    public bool DryRun { get; set; }

    /// <summary>Failure detail, or extra context for cycle-level entries.</summary>
    public string Message { get; set; } = string.Empty;
}

public static class FirewallHistoryActions
{
    public const string RuleCreated = "rule-created";
    public const string RuleUpdated = "rule-updated";
    public const string RuleRemoved = "rule-removed";
    public const string SyncCompleted = "sync-completed";
    public const string SyncFailed = "sync-failed";
    public const string RemoveAll = "remove-all";
}
