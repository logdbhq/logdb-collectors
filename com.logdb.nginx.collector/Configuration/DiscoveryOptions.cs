namespace com.logdb.nginx.collector.Configuration;

/// <summary>
/// Auto-discovery of nginx log files. When enabled, the collector scans a
/// directory and tails every file matching the access/error glob patterns
/// (minus the excludes), in addition to any explicitly configured targets.
/// This avoids hand-listing one target per vhost and automatically picks up
/// new vhost logs as they appear.
///
/// Seeded from the "Discovery" config section; runtime changes made in the UI
/// are persisted to discovery-settings.json and take precedence.
/// </summary>
public class DiscoveryOptions
{
    public const string Section = "Discovery";

    /// <summary>Master switch. When false, only explicitly configured targets are tailed.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Directory to scan for log files.</summary>
    public string Directory { get; set; } = "/var/log/nginx";

    /// <summary>Glob patterns (against the file name) that mark a file as an access log.</summary>
    public List<string> AccessLogPatterns { get; set; } = new() { "*access*.log" };

    /// <summary>Glob patterns (against the file name) that mark a file as an error log.</summary>
    public List<string> ErrorLogPatterns { get; set; } = new() { "*error*.log" };

    /// <summary>Glob patterns to skip entirely (rotated/compressed logs, etc.). Takes precedence over the access/error patterns.</summary>
    public List<string> ExcludePatterns { get; set; } = new() { "*.gz", "*.zip", "*.xz", "*.[0-9]", "*.[0-9].log" };

    /// <summary>
    /// When a file has no checkpoint yet, start tailing at its current end
    /// instead of byte 0. This skips backfilling the existing file (only new
    /// lines are shipped). Applies to every checkpoint-less target, discovered
    /// or explicit. Recommended on busy servers to avoid flooding the pipeline.
    /// </summary>
    public bool StartAtEnd { get; set; } = true;
}
