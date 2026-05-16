namespace com.logdb.nginx.collector.Models;

public class AgentStatus
{
    public string Version { get; set; } = "";
    public string BuildDate { get; set; } = "";
    public string CommitHash { get; set; } = "";
    public string Environment { get; set; } = "";
    public string State { get; set; } = "starting";
    public DateTime StartedUtc { get; set; }
    public TimeSpan Uptime { get; set; }
    public int TargetCount { get; set; }
    public int ActiveFiles { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
