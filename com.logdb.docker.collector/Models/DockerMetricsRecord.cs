namespace com.logdb.docker.collector.Models;

public class DockerMetricsRecord
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string ContainerId { get; set; } = "";
    public string ContainerName { get; set; } = "";
    public string Image { get; set; } = "";
    public string ImageTag { get; set; } = "";
    public string HostName { get; set; } = "";
    public string ContainerState { get; set; } = "";
    public string ContainerStatus { get; set; } = "";
    public string? ComposeProject { get; set; }
    public string? ComposeService { get; set; }
    public Dictionary<string, string> Labels { get; set; } = new();

    // CPU
    public double CpuUsagePercent { get; set; }
    public long CpuTotalUsage { get; set; }
    public long CpuSystemUsage { get; set; }
    public int CpuOnlineCpus { get; set; }

    // Memory
    public long MemoryUsageBytes { get; set; }
    public long MemoryLimitBytes { get; set; }
    public double MemoryUsagePercent { get; set; }
    public long MemoryMaxUsageBytes { get; set; }

    // Network
    public long NetworkRxBytes { get; set; }
    public long NetworkTxBytes { get; set; }
    public long NetworkRxPackets { get; set; }
    public long NetworkTxPackets { get; set; }

    // Block I/O
    public long BlockIoReadBytes { get; set; }
    public long BlockIoWriteBytes { get; set; }

    // Process
    public int PidsCurrent { get; set; }

    // Health
    public string? HealthStatus { get; set; }
    public int RestartCount { get; set; }
}
