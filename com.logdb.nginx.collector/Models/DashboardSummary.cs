namespace com.logdb.nginx.collector.Models;

public class DashboardSummary
{
    public AgentStatus Agent { get; set; } = new();
    public TargetsSummary Targets { get; set; } = new();
    public PipelineSummary Pipeline { get; set; } = new();
    public ExporterSummary Exporter { get; set; } = new();
    public SpoolSummary Spool { get; set; } = new();
}

public class TargetsSummary
{
    public int ConfiguredTargets { get; set; }
    public int EnabledTargets { get; set; }
    public int ActiveFiles { get; set; }
    public List<string> MissingFiles { get; set; } = new();
}

public class PipelineSummary
{
    public int ActiveTargets { get; set; }
    public int ActiveFiles { get; set; }
    public long AccessRecordsRead { get; set; }
    public long ErrorRecordsRead { get; set; }
    public long ParseErrors { get; set; }
    public long ReadErrors { get; set; }
    public long FilteredStaticFiles { get; set; }
    public long FilteredByRules { get; set; }
    public long RotationsDetected { get; set; }
    public DateTime? LastRecordTimestamp { get; set; }
    public DateTime? LastTailCycleUtc { get; set; }
}

public class ExporterSummary
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "";
    public bool ApiKeyConfigured { get; set; }
    public bool Healthy { get; set; }
    public long BatchesSent { get; set; }
    public long RecordsSent { get; set; }
    public long SendErrors { get; set; }
    public long RetryCount { get; set; }
    public DateTime? LastSendUtc { get; set; }
    public string? LastError { get; set; }
    public int FlushIntervalSeconds { get; set; }
    public int FlushIntervalMinSeconds { get; set; }
    public int FlushIntervalMaxSeconds { get; set; }
}

public class SpoolSummary
{
    public bool Enabled { get; set; }
    public long QueuedRecords { get; set; }
    public long DiskBytesUsed { get; set; }
    public long MaxDiskBytes { get; set; }
    public double UtilizationPercent { get; set; }
    public long DroppedRecords { get; set; }
    public long ReplayedRecords { get; set; }
    public string? LastError { get; set; }
}
