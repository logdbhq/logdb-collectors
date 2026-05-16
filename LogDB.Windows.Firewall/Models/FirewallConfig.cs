namespace LogDB.Windows.Firewall.Models;

public class FirewallConfig
{
    public int SyncIntervalMinutes { get; set; } = 15;
    public string RulePrefix { get; set; } = "LogDB Firewall";
    public string Direction { get; set; } = "Inbound";
    public bool DryRun { get; set; } = false;
    public int MaxIpsPerRule { get; set; } = 5000;
    public Dictionary<string, PublicBlocklistConfig> PublicBlocklists { get; set; } = new();
    public CustomBlocklistConfig CustomBlocklist { get; set; } = new();
    public string WhitelistPath { get; set; } = "";
}

public class PublicBlocklistConfig
{
    public bool Enabled { get; set; } = true;
    public string Url { get; set; } = "";
    public int MinScore { get; set; } = 0;
}

public class CustomBlocklistConfig
{
    public bool Enabled { get; set; } = false;
}

public class LogDbConfig
{
    public string ApiKey { get; set; } = "";
    public string GuardUrl { get; set; } = "";
}

public class ServerConfig
{
    public string ServerName { get; set; } = "";
    public string ServerEnvironment { get; set; } = "Production";
}
