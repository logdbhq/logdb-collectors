namespace com.logdb.docker.collector.Models;

public class DashboardSummary
{
    public AgentStatus Agent { get; set; } = new();
    public DockerSummary Docker { get; set; } = new();
    public PipelineSummary Pipeline { get; set; } = new();
    public ExporterSummary Exporter { get; set; } = new();
    public SpoolSummary Spool { get; set; } = new();
}

public class DockerSummary
{
    public bool Available { get; set; }
    public string Endpoint { get; set; } = "";
    public int ContainerCount { get; set; }
    public int IncludedCount { get; set; }
    public string? Error { get; set; }
}

public class PipelineSummary
{
    public int ActiveTargets { get; set; }
    public long RecordsRead { get; set; }
    public long ParseErrors { get; set; }
    public long ReadErrors { get; set; }
    public long FilteredByMessage { get; set; }
    public long FilteredByCategory { get; set; }
    public DateTime? LastRecordTimestamp { get; set; }
}

public class ExporterSummary
{
    public bool Enabled { get; set; }
    public bool Healthy { get; set; }
    public long BatchesSent { get; set; }
    public long RecordsSent { get; set; }
    public long SendErrors { get; set; }
    public DateTime? LastSendUtc { get; set; }
    public string? LastError { get; set; }
}

public class SpoolSummary
{
    public bool Enabled { get; set; }
    public long QueuedRecords { get; set; }
    public long DiskBytesUsed { get; set; }
    public long MaxDiskBytes { get; set; }
    public int UtilizationPercent { get; set; }
    public long DroppedRecords { get; set; }
    public long ReplayedRecords { get; set; }
    public string? LastError { get; set; }
}
