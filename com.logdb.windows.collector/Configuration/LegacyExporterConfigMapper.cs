using System.Text.Json;
using com.logdb.windows.collector.shared.Contracts;

namespace com.logdb.windows.collector.Configuration;

internal static class LegacyExporterConfigMapper
{
    public static Dictionary<string, string?> BuildEventLogConfig(CollectorConfigDto config)
    {
        var values = BuildCommonValues(config);
        var module = config.Modules.EventLog;

        values["EventViewer:ExportIntervalMinutes"] = MinutesFromSeconds(module.PollIntervalSeconds).ToString();
        values["EventViewer:MaxEventsPerExport"] = "1000";
        values["EventViewer:ApplicationName"] = ResolveApplicationName(config, "Windows Event Viewer");
        values["EventViewer:IncludeXmlDetails"] = "false";

        AddList(values, "EventViewer:LogSources", module.SourcesChannels);
        AddList(values, "EventViewer:EventLevels", module.LevelFilters);
        AddList(values, "EventViewer:Labels", config.Server.DefaultLabels.Concat(new[] { "event-viewer" }));

        return values;
    }

    public static Dictionary<string, string?> BuildIisConfig(CollectorConfigDto config)
    {
        var values = BuildCommonValues(config);
        var module = config.Modules.IIS;

        values["IIS:ExportIntervalMinutes"] = MinutesFromSeconds(module.PollIntervalSeconds).ToString();
        values["IIS:ApplicationName"] = ResolveApplicationName(config, "IIS");
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

    private static Dictionary<string, string?> BuildCommonValues(CollectorConfigDto config)
    {
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Server:ServerName"] = string.IsNullOrWhiteSpace(config.Server.ServerName)
                ? Environment.MachineName
                : config.Server.ServerName,
            ["Server:ServerEnvironment"] = ResolveEnvironmentName(config),
            ["LogDB:DefaultApplication"] = config.LogDB.DefaultApplication,
            ["LogDB:DefaultEnvironment"] = config.LogDB.DefaultEnvironment,
            ["LogDB:DefaultCollection"] = config.LogDB.DefaultCollection
        };
    }

    private static string ResolveApplicationName(CollectorConfigDto config, string fallback)
    {
        return string.IsNullOrWhiteSpace(config.LogDB.DefaultApplication)
            ? fallback
            : config.LogDB.DefaultApplication;
    }

    private static string ResolveEnvironmentName(CollectorConfigDto config)
    {
        if (!string.IsNullOrWhiteSpace(config.LogDB.DefaultEnvironment))
        {
            return config.LogDB.DefaultEnvironment;
        }

        return string.IsNullOrWhiteSpace(config.Server.ServerEnvironment)
            ? "Production"
            : config.Server.ServerEnvironment;
    }

    private static string ResolveCollectionName(CollectorConfigDto config, string fallback)
    {
        return string.IsNullOrWhiteSpace(config.LogDB.DefaultCollection)
            ? fallback
            : config.LogDB.DefaultCollection;
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
