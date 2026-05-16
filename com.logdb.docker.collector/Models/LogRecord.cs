namespace com.logdb.docker.collector.Models;

public class LogRecord
{
    public DateTime Timestamp { get; set; }
    public string Message { get; set; } = "";
    public string Stream { get; set; } = "";
    public string ContainerId { get; set; } = "";
    public string ContainerName { get; set; } = "";
    public string Image { get; set; } = "";
    public string HostName { get; set; } = "";
    public string SourceType { get; set; } = "docker";
    public Dictionary<string, string> Labels { get; set; } = new();
    public string? ComposeProject { get; set; }
    public string? ComposeService { get; set; }

    /// <summary>Parsed from .NET ConsoleLogger header, e.g. "com.logdb.guard.Services.GuardGrpcService"</summary>
    public string? Category { get; set; }

    /// <summary>Parsed from .NET ConsoleLogger prefix: info/warn/fail/dbug/crit/trce</summary>
    public string? ParsedLevel { get; set; }
}
