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

public sealed class DeleteFirewallRuleRequestDto
{
    /// <summary>The rule's unique Name (<see cref="FirewallRuleInfoDto.Id"/>).</summary>
    public string RuleId { get; set; } = string.Empty;

    /// <summary>When true and the rule is Guard-sourced, its IPs are also
    /// removed from the LogDB Guard backend so the next sync doesn't
    /// re-block them.</summary>
    public bool RemoveFromBackend { get; set; } = true;
}
