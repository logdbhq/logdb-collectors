namespace com.logdb.docker.collector.Models;

/// <summary>
/// One record the exporter handed to grpc-logger, captured with detail and its
/// delivery outcome. Covers both container log records and container metric
/// records — <see cref="Kind"/> distinguishes them.
///
/// "delivered" means the batch this record belonged to flushed to grpc-logger
/// without error. It does NOT guarantee the record survived the downstream
/// Kafka -> consumer -> database hop.
/// </summary>
public class SentRecordEntry
{
    public long Id { get; set; }

    /// <summary>When the exporter sent this record to grpc-logger.</summary>
    public DateTime SentUtc { get; set; }

    /// <summary>The record's own timestamp (log line time, or metric collection time).</summary>
    public DateTime RecordTimestamp { get; set; }

    /// <summary>"delivered" or "failed".</summary>
    public string Outcome { get; set; } = "";

    /// <summary>"log" or "metric".</summary>
    public string Kind { get; set; } = "log";

    /// <summary>
    /// For logs: how many raw records this event represents after same-second
    /// aggregation (1 = sent as-is). Always 1 for metrics.
    /// </summary>
    public int AggregatedCount { get; set; } = 1;

    public string ContainerId { get; set; } = "";
    public string Container { get; set; } = "";
    public string? Image { get; set; }
    public string? ComposeProject { get; set; }
    public string? ComposeService { get; set; }

    // Log fields
    public string? Stream { get; set; }
    public string? Level { get; set; }
    public string? Category { get; set; }

    /// <summary>Log message, or a one-line metric summary (cpu / mem / net).</summary>
    public string? Message { get; set; }

    // Metric fields (Kind == "metric")
    public double? CpuPercent { get; set; }
    public double? MemoryPercent { get; set; }

    /// <summary>The GUID of the LogDB event actually sent (lets you grep for it downstream).</summary>
    public string Guid { get; set; } = "";

    public string Endpoint { get; set; } = "";

    /// <summary>Short status. "ok" / "ok (N retried)" on delivery, error class on failure.</summary>
    public string? Status { get; set; }

    public string? Error { get; set; }
}

public class SentRecordsSnapshot
{
    public List<SentRecordEntry> Records { get; set; } = new();
    public long TotalSent { get; set; }
    public long TotalDelivered { get; set; }
    public long TotalFailed { get; set; }
    public long TotalLogs { get; set; }
    public long TotalMetrics { get; set; }
    public int BufferSize { get; set; }
    public int BufferUsed { get; set; }
}
