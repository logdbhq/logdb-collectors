using com.logdb.windows.collector.shared.Contracts;

namespace com.logdb.windows.collector.shared.Services;

public static class CollectorConfigRedactor
{
    public static CollectorConfigDto CreateRedacted(CollectorConfigDto source)
    {
        var copy = new CollectorConfigDto
        {
            LogDB = new LogDbConfigDto
            {
                ApiKey = MaskApiKey(source.LogDB.ApiKey),
                Endpoint = source.LogDB.Endpoint,
                DiscoveryUrl = source.LogDB.DiscoveryUrl,
                Protocol = source.LogDB.Protocol,
                Retry = new RetryOptionsDto
                {
                    MaxRetries = source.LogDB.Retry.MaxRetries,
                    EnableCircuitBreaker = source.LogDB.Retry.EnableCircuitBreaker
                },
                Batch = new BatchOptionsDto
                {
                    EnableBatching = source.LogDB.Batch.EnableBatching,
                    BatchSize = source.LogDB.Batch.BatchSize,
                    FlushIntervalSeconds = source.LogDB.Batch.FlushIntervalSeconds,
                    EnableCompression = source.LogDB.Batch.EnableCompression
                }
            },
            Server = new ServerConfigDto
            {
                ServerName = source.Server.ServerName,
                ServerEnvironment = source.Server.ServerEnvironment,
                DefaultLabels = new List<string>(source.Server.DefaultLabels)
            },
            Modules = new ModulesConfigDto
            {
                EventLog = new EventLogModuleConfigDto
                {
                    Enabled = source.Modules.EventLog.Enabled,
                    PollIntervalSeconds = source.Modules.EventLog.PollIntervalSeconds,
                    SourcesChannels = new List<string>(source.Modules.EventLog.SourcesChannels),
                    LevelFilters = new List<string>(source.Modules.EventLog.LevelFilters),
                    ResetState = source.Modules.EventLog.ResetState,
                    InitialStartDateUtc = source.Modules.EventLog.InitialStartDateUtc
                },
                IIS = new IisModuleConfigDto
                {
                    Enabled = source.Modules.IIS.Enabled,
                    PollIntervalSeconds = source.Modules.IIS.PollIntervalSeconds,
                    LogDirectories = new List<string>(source.Modules.IIS.LogDirectories),
                    StateFilePath = source.Modules.IIS.StateFilePath,
                    ResetState = source.Modules.IIS.ResetState,
                    InitialStartDateUtc = source.Modules.IIS.InitialStartDateUtc,
                    SiteName = source.Modules.IIS.SiteName,
                    Include4xx = source.Modules.IIS.Include4xx,
                    Include5xx = source.Modules.IIS.Include5xx
                },
                Metrics = new MetricsModuleConfigDto
                {
                    Enabled = source.Modules.Metrics.Enabled,
                    PollIntervalSeconds = source.Modules.Metrics.PollIntervalSeconds,
                    IncludeCpu = source.Modules.Metrics.IncludeCpu,
                    IncludeMemory = source.Modules.Metrics.IncludeMemory,
                    IncludeDisk = source.Modules.Metrics.IncludeDisk,
                    IncludeNetwork = source.Modules.Metrics.IncludeNetwork,
                    Tags = new Dictionary<string, string>(source.Modules.Metrics.Tags)
                }
            },
            Firewall = new FirewallConfigDto
            {
                Enabled = source.Firewall.Enabled,
                PollIntervalSeconds = source.Firewall.PollIntervalSeconds,
                RuleNamePrefix = source.Firewall.RuleNamePrefix
            }
        };

        return copy;
    }

    private static string MaskApiKey(string apiKey)
    {
        return string.IsNullOrEmpty(apiKey) ? string.Empty : "********";
    }
}
