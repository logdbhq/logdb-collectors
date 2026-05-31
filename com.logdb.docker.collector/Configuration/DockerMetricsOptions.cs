namespace com.logdb.docker.collector.Configuration;

public class DockerMetricsOptions
{
    public const string Section = "DockerMetrics";

    public bool Enabled { get; set; } = true;
    public int CollectionIntervalSeconds { get; set; } = 300;
    public int MaxConcurrentStats { get; set; } = 8;
    public int StatsTimeoutSeconds { get; set; } = 5;
    public bool IncludeStoppedContainers { get; set; } = false;
    public bool IncludeHealthCheck { get; set; } = true;
}
