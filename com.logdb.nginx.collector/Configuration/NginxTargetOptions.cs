namespace com.logdb.nginx.collector.Configuration;

public class NginxTargetOptions
{
    public const string Section = "NginxTargets";

    public List<NginxTarget> Targets { get; set; } = new();

    /// <summary>
    /// When true, access log requests for static files are excluded from export.
    /// Static files are identified by path extension matching ExcludeExtensions.
    /// Error logs are never filtered. Default: true.
    /// </summary>
    public bool ExcludeStaticFiles { get; set; } = true;

    /// <summary>
    /// File extensions to exclude when ExcludeStaticFiles is enabled.
    /// Matched case-insensitively against the request path.
    /// </summary>
    public HashSet<string> ExcludeExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".js", ".mjs",
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".webp", ".bmp", ".avif",
        ".woff", ".woff2", ".ttf", ".eot", ".otf",
        ".map", ".less", ".scss", ".ts",
        ".mp4", ".webm", ".ogg", ".mp3", ".wav",
        ".pdf",
        ".xml", ".txt", ".robots"
    };

    /// <summary>
    /// Path prefixes to exclude from access logs (case-insensitive, prefix match).
    /// E.g. "/health", "/api/status", "/favicon".
    /// </summary>
    public List<string> ExcludePaths { get; set; } = new();

    /// <summary>
    /// Remote addresses (IPs) to exclude from access logs (exact match).
    /// E.g. "127.0.0.1", "10.0.0.1" for health check probes.
    /// </summary>
    public List<string> ExcludeRemoteAddresses { get; set; } = new();
}

public class NginxTarget
{
    public string Name { get; set; } = "";
    public string AccessLogPath { get; set; } = "";
    public string ErrorLogPath { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
