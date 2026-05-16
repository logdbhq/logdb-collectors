namespace com.logdb.windows.iis.Models;

/// <summary>
/// Log format for IIS log sources.
/// </summary>
public enum LogFormat
{
    /// <summary>Auto-detect format from directory structure and file extensions</summary>
    Auto,
    /// <summary>Standard IIS W3C extended log format (.log files)</summary>
    W3C,
    /// <summary>Azure App Service JSON/NDJSON format (.json files)</summary>
    AzureJson
}

/// <summary>
/// A log source with explicit path and format.
/// </summary>
public class LogSource
{
    /// <summary>Path to IIS log directory or files (supports wildcards like W3SVC*)</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Log format: Auto, W3C, or AzureJson</summary>
    public LogFormat Format { get; set; } = LogFormat.Auto;
}

/// <summary>
/// Local configuration for IIS log export, read from appsettings.json.
/// Replaces the remote iis_log_config database model.
/// </summary>
public class IISExportConfig
{
    /// <summary>Simple path list (backward compat, treated as Format=Auto). Use LogSources for explicit format control.</summary>
    public List<string> LogPaths { get; set; } = new();

    /// <summary>Log sources with explicit format per path. Takes precedence over LogPaths.</summary>
    public List<LogSource> LogSources { get; set; } = new();

    /// <summary>Build a merged list of all log sources (LogSources + LogPaths as Auto)</summary>
    public List<LogSource> GetEffectiveSources()
    {
        var sources = new List<LogSource>();

        // LogSources (explicit) first
        sources.AddRange(LogSources.Where(s => !string.IsNullOrWhiteSpace(s.Path)));

        // LogPaths (backward compat) as Auto
        foreach (var path in LogPaths)
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                !sources.Any(s => s.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                sources.Add(new LogSource { Path = path, Format = LogFormat.Auto });
            }
        }

        return sources;
    }

    /// <summary>Application name in LogDB</summary>
    public string ApplicationName { get; set; } = "IIS";

    /// <summary>Collection name in LogDB (if empty, auto-generates from folder name)</summary>
    public string? Collection { get; set; }

    /// <summary>Map IIS site names to specific LogDB collections</summary>
    public Dictionary<string, string>? CollectionMap { get; set; }

    /// <summary>Labels attached to each log entry</summary>
    public List<string> Labels { get; set; } = new() { "iis", "web-server" };

    /// <summary>Export cycle interval in minutes</summary>
    public int ExportIntervalMinutes { get; set; } = 1;

    // --- Directory filters (applied to parent folder names like W3SVC1, W3SVC2) ---

    /// <summary>Only process log files from these directories (empty = all)</summary>
    public List<string>? IncludeDirectories { get; set; }

    /// <summary>Skip log files from these directories</summary>
    public List<string>? ExcludeDirectories { get; set; }

    // --- Entry filters ---

    /// <summary>Only include entries with these IIS site names</summary>
    public List<string>? SiteNames { get; set; }

    /// <summary>Only include entries with these HTTP status codes</summary>
    public List<int>? IncludeStatusCodes { get; set; }

    /// <summary>Exclude entries with these HTTP status codes</summary>
    public List<int>? ExcludeStatusCodes { get; set; }

    /// <summary>Exclude entries whose URI ends with these extensions (e.g. ".css", ".js")</summary>
    public List<string>? ExcludeExtensions { get; set; }

    /// <summary>Exclude entries whose URI starts with these paths</summary>
    public List<string>? ExcludePaths { get; set; }

    /// <summary>Advanced filter conditions as JSON (min/max time, user agents, IPs, methods, etc.)</summary>
    public string? FilterConditions { get; set; }
}
