namespace com.logdb.docker.collector.Models;

public class DockerStatus
{
    public bool Available { get; set; }
    public string Endpoint { get; set; } = "";
    public DateTime? LastRefreshUtc { get; set; }
    public int ContainerCount { get; set; }
    public string? Error { get; set; }
}
