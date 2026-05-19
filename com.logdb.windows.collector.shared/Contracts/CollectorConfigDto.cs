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
    // Default to uncompressed: many self-hosted LogDB deployments don't fully
    // implement the SendCompressedLog* RPCs and silently drop rows even though
    // the gRPC call returns Success. The plain Log / LogBeat handlers are
    // universally supported. Users on a deployment with working compression
    // can flip this back to true via the Advanced JSON editor.
    public bool EnableCompression { get; set; } = false;
}

public class ModulesConfigDto
{
    public EventLogModuleConfigDto EventLog { get; set; } = new();
    public IisModuleConfigDto IIS { get; set; } = new();
    public MetricsModuleConfigDto Metrics { get; set; } = new();
    public HeartbeatModuleConfigDto Heartbeat { get; set; } = new();
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
    public List<EventLogFilterRuleDto> FilterRules { get; set; } = new();

    /// <summary>
    /// Optional Provider (Source) name override applied to every ingested event
    /// — both real Windows event-log entries AND the synthetic event the Test
    /// button sends. Useful for tagging which server logs are coming from.
    /// Leave blank to keep each event's original Provider (Test falls back to
    /// "LogDB.UI.Test").
    /// </summary>
    public string? ProviderNameOverride { get; set; }
}

public class EventLogFilterRuleDto
{
    public string Field { get; set; } = EventLogFilterFields.EventId;
    public string Value { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public static class EventLogFilterFields
{
    public const string EventId = "EventId";
    public const string SourceContains = "SourceContains";
    public const string MessageContains = "MessageContains";
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

    /// <summary>
    /// Optional Server name override applied to metric rows only. When set, this
    /// value replaces the global Server:ServerName for every metric the tracker
    /// emits (CPU / memory / disk / network) AND the Test button. Useful for
    /// tagging which server / collector instance the metrics are coming from.
    /// Leave blank to use the global server name from the Destination page.
    /// </summary>
    public string? ServerNameOverride { get; set; }
}

public class HeartbeatModuleConfigDto : ModuleConfigDto
{
    public string Measurement { get; set; } = "heartbeat";
    public string Collection { get; set; } = "beats";

    // Each built-in field is an opt-in toggle the user controls in the UI.
    public bool IncludeUptime { get; set; } = true;
    public bool IncludeHostnameTag { get; set; } = true;
    public bool IncludeAppVersionTag { get; set; }
    public bool IncludeCpuPercent { get; set; }
    public bool IncludeMemoryPercent { get; set; }

    // User-defined extra tags (key → value). Mirrors MetricsModuleConfigDto.Tags.
    public Dictionary<string, string> Tags { get; set; } = new();

    /// <summary>
    /// Optional Server name override applied to every emitted beat (and the
    /// Test). When set, replaces Server:ServerName for the Heartbeat module —
    /// affects the "host" tag. Leave blank to use the global server name.
    /// </summary>
    public string? ServerNameOverride { get; set; }

    /// <summary>
    /// Optional Environment override applied to every emitted beat (and the
    /// Test). When set, replaces LogDB:DefaultEnvironment for the Heartbeat
    /// module only. Leave blank to use the global Environment.
    /// </summary>
    public string? EnvironmentOverride { get; set; }
}

public class FirewallConfigDto
{
    public bool Enabled { get; set; }
    public int PollIntervalSeconds { get; set; } = 60;
    public string RuleNamePrefix { get; set; } = "LogDB Block";
}
