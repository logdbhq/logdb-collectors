namespace com.logdb.nginx.collector.Models;

public class NginxLogRecord
{
    public DateTime Timestamp { get; set; }
    public string Message { get; set; } = "";
    public NginxLogType LogType { get; set; }
    public string TargetName { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public string HostName { get; set; } = "";

    // Access log fields
    public string? RemoteAddress { get; set; }
    public string? Method { get; set; }
    public string? Path { get; set; }
    public string? Protocol { get; set; }
    public int? StatusCode { get; set; }
    public long? ResponseBytes { get; set; }
    public string? Referer { get; set; }
    public string? UserAgent { get; set; }
    public double? RequestTime { get; set; }
    public string? ServerName { get; set; }

    // Error log fields
    public string? Severity { get; set; }
    public int? Pid { get; set; }
    public int? Tid { get; set; }
    public long? ConnectionId { get; set; }
    public string? Upstream { get; set; }
}

public enum NginxLogType
{
    Access,
    Error
}
