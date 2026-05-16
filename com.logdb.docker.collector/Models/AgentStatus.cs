namespace com.logdb.docker.collector.Models;

public class AgentStatus
{
    public string Service { get; set; } = "LogDB Docker Collector Agent";
    public string Version { get; set; } = "0.1";
    public string? BuildDate { get; set; }
    public string? CommitHash { get; set; }
    public string? Environment { get; set; }
    public string? DockerEndpoint { get; set; }
    public double UptimeSeconds { get; set; }
    public string AgentState { get; set; } = "running";
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}
