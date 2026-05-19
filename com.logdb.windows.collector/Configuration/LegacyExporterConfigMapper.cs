using System.Text.Json;
using com.logdb.windows.collector.shared.Contracts;

namespace com.logdb.windows.collector.Configuration;

internal static class LegacyExporterConfigMapper
{
    public static Dictionary<string, string?> BuildEventLogConfig(CollectorConfigDto config)
    {
        var values = BuildCommonValues(config);
        var module = config.Modules.EventLog;

        // Per-module Server name override. Rewriting Server:ServerName here scopes the
        // override to the EventLog module — IIS / Metrics / Heartbeat still see the
        // global Server:ServerName. The signal key Server:ServerNameOverride tells
        // EventViewerExportService that the user explicitly opted in to override the
        // Computer field (otherwise it keeps the raw eventEntry.MachineName).
        if (!string.IsNullOrWhiteSpace(module.ServerNameOverride))
        {
            var serverNameOverride = module.ServerNameOverride!.Trim();
            values["Server:ServerName"] = serverNameOverride;
            values["Server:ServerNameOverride"] = serverNameOverride;
        }

        values["EventViewer:ExportIntervalMinutes"] = MinutesFromSeconds(module.PollIntervalSeconds).ToString();
        values["EventViewer:MaxEventsPerExport"] = "1000";
        values["EventViewer:ApplicationName"] = "Windows Event Viewer";
        values["EventViewer:IncludeXmlDetails"] = "false";
        if (!string.IsNullOrWhiteSpace(module.ProviderNameOverride))
        {
            values["EventViewer:ProviderNameOverride"] = module.ProviderNameOverride!.Trim();
        }

        AddList(values, "EventViewer:LogSources", module.SourcesChannels);
        AddList(values, "EventViewer:EventLevels", module.LevelFilters);
        AddList(values, "EventViewer:Labels", config.Server.DefaultLabels.Concat(new[] { "event-viewer" }));

        ApplyEventLogFilters(values, module);

        return values;
    }

    private static void ApplyEventLogFilters(IDictionary<string, string?> values, EventLogModuleConfigDto module)
    {
        var excludeEventIds = new List<int>();
        var excludeSourceContains = new List<string>();
        var excludeKeywords = new List<string>();

        foreach (var rule in module.FilterRules ?? Enumerable.Empty<EventLogFilterRuleDto>())
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Value)) continue;
            var value = rule.Value.Trim();
            switch (rule.Field)
            {
                case EventLogFilterFields.EventId:
                    if (int.TryParse(value, out var id)) excludeEventIds.Add(id);
                    break;
                case EventLogFilterFields.SourceContains:
                    excludeSourceContains.Add(value);
                    break;
                case EventLogFilterFields.MessageContains:
                    excludeKeywords.Add(value);
                    break;
            }
        }

        if (excludeEventIds.Count == 0 && excludeSourceContains.Count == 0 && excludeKeywords.Count == 0)
            return;

        var filter = new Dictionary<string, object?>();
        if (excludeEventIds.Count > 0)
            filter["ExcludeEventIds"] = excludeEventIds.Distinct().ToList();
        if (excludeSourceContains.Count > 0)
            filter["ExcludeSourceContains"] = excludeSourceContains.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (excludeKeywords.Count > 0)
            filter["ExcludeKeywords"] = excludeKeywords.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        values["EventViewer:FilterConditions"] = JsonSerializer.Serialize(filter);
    }

    public static Dictionary<string, string?> BuildIisConfig(CollectorConfigDto config)
    {
        var values = BuildCommonValues(config);
        var module = config.Modules.IIS;

        // Per-module Server name override. The IIS exporter reads
        // Server:ServerName for general identity AND uses the dedicated
        // Server:ServerNameOverride signal key to know when to also rewrite
        // the typed LogIISEvent.ServerName field (which defaults to the W3C
        // log's s-computername). Setting both: scopes override + opt-in to
        // wire-level field rewrite — EventLog / Metrics / Heartbeat untouched.
        if (!string.IsNullOrWhiteSpace(module.ServerNameOverride))
        {
            var serverNameOverride = module.ServerNameOverride!.Trim();
            values["Server:ServerName"] = serverNameOverride;
            values["Server:ServerNameOverride"] = serverNameOverride;
        }

        values["IIS:ExportIntervalMinutes"] = MinutesFromSeconds(module.PollIntervalSeconds).ToString();
        values["IIS:ApplicationName"] = "IIS";
        AddList(values, "IIS:LogPaths", module.LogDirectories);
        AddList(values, "IIS:Labels", config.Server.DefaultLabels.Concat(new[] { "iis" }));

        if (!string.IsNullOrWhiteSpace(module.SiteName))
        {
            AddList(values, "IIS:SiteNames", new[] { module.SiteName.Trim() });
        }

        ApplyIisFilters(values, module);

        return values;
    }

    private static void ApplyIisFilters(IDictionary<string, string?> values, IisModuleConfigDto module)
    {
        var excludeExtensions = new List<string>();
        var excludePaths = new List<string>();
        var excludeStatusCodes = new List<int>();
        var excludeMethods = new List<string>();
        var excludeUserAgentWildcards = new List<string>();
        var excludeIps = new List<string>();
        var excludeIpRanges = new List<string>();
        int? minTime = null;
        int? maxTime = null;

        if (module.ExcludeStaticFiles)
        {
            excludeExtensions.AddRange(new[]
            {
                ".css", ".js", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico",
                ".woff", ".woff2", ".ttf", ".eot", ".map", ".webp", ".bmp",
                ".less", ".scss", ".ts"
            });
        }

        foreach (var rule in module.FilterRules ?? Enumerable.Empty<IisFilterRuleDto>())
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Value))
            {
                continue;
            }

            var value = rule.Value.Trim();
            switch (rule.Field)
            {
                case IisFilterFields.PathPrefix:
                    excludePaths.Add(value);
                    break;
                case IisFilterFields.Extension:
                    excludeExtensions.Add(value.StartsWith('.') ? value : "." + value);
                    break;
                case IisFilterFields.StatusCode:
                    if (int.TryParse(value, out var code))
                    {
                        excludeStatusCodes.Add(code);
                    }
                    break;
                case IisFilterFields.Method:
                    excludeMethods.Add(value.ToUpperInvariant());
                    break;
                case IisFilterFields.UserAgentContains:
                    // IISLogFilter uses wildcard matching; wrap substring in *...*
                    excludeUserAgentWildcards.Add(value.Contains('*') ? value : $"*{value}*");
                    break;
                case IisFilterFields.ClientIp:
                    excludeIps.Add(value);
                    break;
                case IisFilterFields.ClientIpPrefix:
                    excludeIpRanges.Add(value);
                    break;
                case IisFilterFields.MinTimeMs:
                    if (int.TryParse(value, out var minMs))
                    {
                        minTime = minTime.HasValue ? Math.Max(minTime.Value, minMs) : minMs;
                    }
                    break;
                case IisFilterFields.MaxTimeMs:
                    if (int.TryParse(value, out var maxMs))
                    {
                        maxTime = maxTime.HasValue ? Math.Min(maxTime.Value, maxMs) : maxMs;
                    }
                    break;
            }
        }

        AddList(values, "IIS:ExcludeExtensions", excludeExtensions.Distinct(StringComparer.OrdinalIgnoreCase));
        AddList(values, "IIS:ExcludePaths", excludePaths.Distinct(StringComparer.OrdinalIgnoreCase));
        AddList(values, "IIS:ExcludeStatusCodes", excludeStatusCodes.Distinct().Select(c => c.ToString()));

        var advanced = new Dictionary<string, object?>();
        if (excludeMethods.Count > 0) advanced["ExcludeMethods"] = excludeMethods.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (excludeUserAgentWildcards.Count > 0) advanced["ExcludeUserAgents"] = excludeUserAgentWildcards.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (excludeIps.Count > 0) advanced["ExcludeIps"] = excludeIps.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (excludeIpRanges.Count > 0) advanced["ExcludeIpRanges"] = excludeIpRanges.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (minTime.HasValue) advanced["MinTimeTaken"] = minTime.Value;
        if (maxTime.HasValue) advanced["MaxTimeTaken"] = maxTime.Value;

        if (advanced.Count > 0)
        {
            values["IIS:FilterConditions"] = JsonSerializer.Serialize(advanced);
        }
    }

    public static Dictionary<string, string?> BuildMetricsConfig(CollectorConfigDto config)
    {
        var values = BuildCommonValues(config);
        var module = config.Modules.Metrics;

        // Per-module Server name override. The tracker reads Server:ServerName directly,
        // so the cleanest path is to replace that key here for the Metrics module only —
        // EventLog / IIS still see the global Server:ServerName.
        if (!string.IsNullOrWhiteSpace(module.ServerNameOverride))
        {
            values["Server:ServerName"] = module.ServerNameOverride!.Trim();
        }

        values["WindowsTracker:CollectionIntervalSeconds"] = Math.Max(5, module.PollIntervalSeconds).ToString();
        values["WindowsTracker:Collection"] = "windows-metrics";
        values["WindowsTracker:Metrics:CPU"] = module.IncludeCpu.ToString();
        values["WindowsTracker:Metrics:Memory"] = module.IncludeMemory.ToString();
        values["WindowsTracker:Metrics:Disk"] = module.IncludeDisk.ToString();
        values["WindowsTracker:Metrics:Network"] = module.IncludeNetwork.ToString();

        var labels = new List<string>(config.Server.DefaultLabels);
        labels.Add("windows-tracker");
        labels.AddRange(module.Tags.Select(tag => $"{tag.Key}:{tag.Value}"));
        AddList(values, "Server:DefaultLabels", labels.Distinct(StringComparer.OrdinalIgnoreCase));

        return values;
    }

    public static Dictionary<string, string?> BuildHeartbeatConfig(CollectorConfigDto config)
    {
        var values = BuildCommonValues(config);
        var module = config.Modules.Heartbeat;

        // Per-module overrides — replace the values inherited from BuildCommonValues so
        // HeartbeatBeatExportService picks up the user's tags without needing extra config
        // keys. Only the Heartbeat module is affected; EventLog / IIS / Metrics still see
        // the global Server:ServerName and Server:ServerEnvironment. The signal keys
        // (Server:ServerNameOverride / Server:ServerEnvironmentOverride) are written only
        // when the user explicitly set the override — mirrors EventLog / IIS so the
        // operator can verify at-a-glance which typed override survived.
        if (!string.IsNullOrWhiteSpace(module.ServerNameOverride))
        {
            var serverNameOverride = module.ServerNameOverride!.Trim();
            values["Server:ServerName"] = serverNameOverride;
            values["Server:ServerNameOverride"] = serverNameOverride;
        }
        if (!string.IsNullOrWhiteSpace(module.EnvironmentOverride))
        {
            var environmentOverride = module.EnvironmentOverride!.Trim();
            values["Server:ServerEnvironment"] = environmentOverride;
            values["Server:ServerEnvironmentOverride"] = environmentOverride;
        }

        values["Heartbeat:IntervalSeconds"] = Math.Max(5, module.PollIntervalSeconds).ToString();
        values["Heartbeat:Measurement"] = string.IsNullOrWhiteSpace(module.Measurement) ? "heartbeat" : module.Measurement;
        values["Heartbeat:Collection"] = string.IsNullOrWhiteSpace(module.Collection) ? "beats" : module.Collection;

        values["Heartbeat:IncludeUptime"] = module.IncludeUptime.ToString();
        values["Heartbeat:IncludeHostnameTag"] = module.IncludeHostnameTag.ToString();
        values["Heartbeat:IncludeAppVersionTag"] = module.IncludeAppVersionTag.ToString();
        values["Heartbeat:IncludeCpuPercent"] = module.IncludeCpuPercent.ToString();
        values["Heartbeat:IncludeMemoryPercent"] = module.IncludeMemoryPercent.ToString();

        var index = 0;
        foreach (var tag in module.Tags)
        {
            if (string.IsNullOrWhiteSpace(tag.Key)) continue;
            values[$"Heartbeat:Tags:{index}:Key"] = tag.Key;
            values[$"Heartbeat:Tags:{index}:Value"] = tag.Value ?? string.Empty;
            index++;
        }

        return values;
    }

    private static Dictionary<string, string?> BuildCommonValues(CollectorConfigDto config)
    {
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Server:ServerName"] = string.IsNullOrWhiteSpace(config.Server.ServerName)
                ? Environment.MachineName
                : config.Server.ServerName,
            ["Server:ServerEnvironment"] = string.IsNullOrWhiteSpace(config.Server.ServerEnvironment)
                ? "Production"
                : config.Server.ServerEnvironment
        };
    }

    private static int MinutesFromSeconds(int seconds)
    {
        return Math.Max(1, (int)Math.Ceiling(Math.Max(1, seconds) / 60d));
    }

    private static void AddList(
        IDictionary<string, string?> target,
        string sectionKey,
        IEnumerable<string> values)
    {
        var index = 0;
        foreach (var value in values)
        {
            target[$"{sectionKey}:{index}"] = value;
            index++;
        }
    }
}
