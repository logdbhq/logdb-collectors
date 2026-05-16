namespace LogDB.Windows.Firewall.Models;

public class FirewallState
{
    public DateTime LastFullSyncUtc { get; set; }
    public int TotalRulesManaged { get; set; }
    public Dictionary<string, SourceSyncState> Sources { get; set; } = new();
}

public class SourceSyncState
{
    public string SourceName { get; set; } = "";
    public DateTime LastSyncUtc { get; set; }
    public int IpCount { get; set; }
    public int RuleCount { get; set; }
    public string Status { get; set; } = "unknown";
    public string ErrorMessage { get; set; } = "";
}
