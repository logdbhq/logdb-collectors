namespace com.logdb.windows.collector.shared.Contracts;

/// <summary>
/// One collector-managed rule as it currently exists in the OS firewall,
/// read back live via ControlCommands.GetFirewallRules. Id is the rule's
/// unique Windows Firewall Name: "LogDB-FW-&lt;hash&gt;" for rules created
/// since the group tagging shipped, an auto-generated GUID for older ones —
/// either way unique, and the handle DeleteFirewallRule takes.
/// </summary>
public sealed class FirewallRuleInfoDto
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Feed display name recovered from the rule name (e.g.
    /// "FireHOL Level 1", "LogDB Guard").</summary>
    public string Source { get; set; } = string.Empty;

    public string Direction { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int IpCount { get; set; }

    /// <summary>True for rules created before group tagging — matched by
    /// display-name prefix instead of the "LogDB Collector" group.</summary>
    public bool Legacy { get; set; }
}

/// <summary>Live RemoteAddress list of one managed rule, read from the OS
/// firewall on demand via ControlCommands.GetFirewallRuleIps — full list,
/// nothing persisted.</summary>
public sealed class FirewallRuleIpsDto
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> Ips { get; set; } = new();
}

/// <summary>One entry of the collector-maintained blocked-IP index: which
/// IP/CIDR is blocked, which feed put it there, and since when. BlockedAtUtc
/// is exact for IPs added after the index shipped; IPs that were already in
/// rules when the index first ran carry the first-observed timestamp.</summary>
public sealed class BlockedIpEntryDto
{
    public string Ip { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime BlockedAtUtc { get; set; }
}

public sealed class BlockedIpQueryDto
{
    /// <summary>Case-insensitive substring matched against IP and source; empty = all.</summary>
    public string Filter { get; set; } = string.Empty;

    /// <summary>Cap on returned entries (server clamps to 1–2000).</summary>
    public int Max { get; set; } = 500;

    /// <summary>Exact source (feed display name) to restrict to; empty = any.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>One of <see cref="BlockedIpKinds"/>. Filtering happens on the
    /// service side so Matched/Total stay accurate above the display cap.</summary>
    public string Kind { get; set; } = BlockedIpKinds.All;
}

public static class BlockedIpKinds
{
    public const string All = "all";

    /// <summary>Everything except the Guard subscription — the public threat feeds.</summary>
    public const string Public = "public";

    /// <summary>Only the LogDB Guard custom blocklist.</summary>
    public const string Guard = "guard";
}

/// <summary>One source in the index, with how many IPs it currently accounts
/// for — used to populate the source picker without a second round trip.</summary>
public sealed class BlockedIpSourceDto
{
    public string Source { get; set; } = string.Empty;
    public int Count { get; set; }
    public bool IsGuard { get; set; }
}

public sealed class BlockedIpListResponseDto
{
    /// <summary>All indexed entries, before filtering.</summary>
    public int Total { get; set; }

    /// <summary>Entries matching the filter (may exceed Entries.Count when capped).</summary>
    public int Matched { get; set; }

    /// <summary>Newest-blocked first.</summary>
    public List<BlockedIpEntryDto> Entries { get; set; } = new();

    /// <summary>Every source in the index (not just the filtered subset), so the
    /// picker keeps its full option list while a narrow filter is applied.</summary>
    public List<BlockedIpSourceDto> Sources { get; set; } = new();
}

public sealed class DeleteFirewallRuleRequestDto
{
    /// <summary>The rule's unique Name (<see cref="FirewallRuleInfoDto.Id"/>).</summary>
    public string RuleId { get; set; } = string.Empty;

    /// <summary>When true and the rule is Guard-sourced, its IPs are also
    /// removed from the LogDB Guard backend so the next sync doesn't
    /// re-block them.</summary>
    public bool RemoveFromBackend { get; set; } = true;
}
