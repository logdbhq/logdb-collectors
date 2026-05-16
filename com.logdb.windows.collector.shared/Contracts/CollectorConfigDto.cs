namespace com.logdb.windows.collector.shared.Contracts;

public class CollectorConfigDto
{
    public LogDbConfigDto LogDB { get; set; } = new();
    public ModulesConfigDto Modules { get; set; } = new();
    public FirewallConfigDto Firewall { get; set; } = new();
    public ServerConfigDto Server { get; set; } = new();
}

public class ServerConfigDto
{
    public string ServerName { get; set; } = Environment.MachineName;
    public string ServerEnvironment { get; set; } = "Production";
    public List<string> DefaultLabels { get; set; } = new() { "windows", "collector" };
}

public class LogDbConfigDto
{
    public string ApiKey { get; set; } = string.Empty;
    public string? Endpoint { get; set; }
    public string? DiscoveryUrl { get; set; } = "https://discovery.logdb.site/resolve/grpc-logger";
    public string Protocol { get; set; } = "Native";
    public string DefaultApplication { get; set; } = "LogDB Collector";
    public string DefaultEnvironment { get; set; } = "Production";
    public string DefaultCollection { get; set; } = "windows";
    public RetryOptionsDto Retry { get; set; } = new();
    public BatchOptionsDto Batch { get; set; } = new();
}

public class RetryOptionsDto
{
    public int MaxRetries { get; set; } = 3;
    public bool EnableCircuitBreaker { get; set; } = true;
}

public class BatchOptionsDto
{
    public bool EnableBatching { get; set; } = false;
    public int BatchSize { get; set; } = 100;
    public int FlushIntervalSeconds { get; set; } = 5;
    public bool EnableCompression { get; set; } = true;
}

public class ModulesConfigDto
{
    public EventLogModuleConfigDto EventLog { get; set; } = new();
    public IisModuleConfigDto IIS { get; set; } = new();
    public MetricsModuleConfigDto Metrics { get; set; } = new();
}

public class ModuleConfigDto
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 60;
}

public class EventLogModuleConfigDto : ModuleConfigDto
{
    public List<string> SourcesChannels { get; set; } = new() { "System", "Application" };
    public List<string> LevelFilters { get; set; } = new() { "error", "warning", "information" };
    public bool ResetState { get; set; }
    public DateTime? InitialStartDateUtc { get; set; }
}

public class IisModuleConfigDto : ModuleConfigDto
{
    public List<string> LogDirectories { get; set; } = new();
    public string? StateFilePath { get; set; }
    public bool ResetState { get; set; }
    public DateTime? InitialStartDateUtc { get; set; }
    public string? SiteName { get; set; }
    public bool Include4xx { get; set; } = true;
    public bool Include5xx { get; set; } = true;
    public bool ExcludeStaticFiles { get; set; }
    public List<IisFilterRuleDto> FilterRules { get; set; } = new();
}

public class IisFilterRuleDto
{
    public string Field { get; set; } = IisFilterFields.PathPrefix;
    public string Value { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public static class IisFilterFields
{
    public const string PathPrefix = "PathPrefix";
    public const string Extension = "Extension";
    public const string StatusCode = "StatusCode";
    public const string Method = "Method";
    public const string UserAgentContains = "UserAgentContains";
    public const string ClientIp = "ClientIp";
    public const string ClientIpPrefix = "ClientIpPrefix";
    public const string MinTimeMs = "MinTimeMs";
    public const string MaxTimeMs = "MaxTimeMs";
}

public class MetricsModuleConfigDto : ModuleConfigDto
{
    public bool IncludeCpu { get; set; } = true;
    public bool IncludeMemory { get; set; } = true;
    public bool IncludeDisk { get; set; } = true;
    public bool IncludeNetwork { get; set; } = true;
    public Dictionary<string, string> Tags { get; set; } = new();
}

public class FirewallConfigDto
{
    public bool Enabled { get; set; }
    public int PollIntervalSeconds { get; set; } = 60;
    public string RuleNamePrefix { get; set; } = "LogDB Block";
}
