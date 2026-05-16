namespace com.logdb.docker.collector.Configuration;

public class DockerDiscoveryOptions
{
    public const string Section = "DockerDiscovery";

    public int RefreshIntervalSeconds { get; set; } = 30;
    public string? DockerEndpoint { get; set; }
}
