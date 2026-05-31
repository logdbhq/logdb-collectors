namespace com.logdb.nginx.collector.Models;

/// <summary>
/// One record (post access-log aggregation) handed to grpc-logger by the exporter,
/// captured with full detail and its delivery outcome. This is the per-record
/// counterpart to <see cref="ExporterCallEntry"/>, which only records batch-level calls.
///
/// "delivered" means the batch this record belonged to was flushed to grpc-logger
/// without error. It does NOT guarantee the record survived the downstream
/// Kafka -> consumer -> database hop.
/// </summary>
public class SentRecordEntry
{
    public long Id { get; set; }

    /// <summary>When the exporter sent this record to grpc-logger.</summary>
    public DateTime SentUtc { get; set; }

    /// <summary>The record's own log timestamp (from the nginx line).</summary>
    public DateTime RecordTimestamp { get; set; }

    /// <summary>"delivered", "failed", or "skipped".</summary>
    public string Outcome { get; set; } = "";

    /// <summary>
    /// How many raw nginx records this single event represents after same-second
    /// aggregation. 1 means it was sent as-is (no collapsing).
    /// </summary>
    public int AggregatedCount { get; set; } = 1;

    public string LogType { get; set; } = "";
    public string Target { get; set; } = "";
    public string? Host { get; set; }
    public string? SourceFile { get; set; }

    // HTTP / access-log fields
    public string? Method { get; set; }
    public string? Path { get; set; }
    public string? Protocol { get; set; }
    public int? StatusCode { get; set; }
    public long? ResponseBytes { get; set; }
    public string? RemoteAddress { get; set; }
    public string? Referer { get; set; }
    public string? UserAgent { get; set; }
    public double? RequestTime { get; set; }

    // Error-log fields
    public string? Severity { get; set; }

    public string? Message { get; set; }

    /// <summary>The GUID of the LogDB event actually sent (lets you grep for it downstream).</summary>
    public string Guid { get; set; } = "";

    public string Endpoint { get; set; } = "";

    /// <summary>First 8 chars of the API key (or "(empty)"/"(placeholder)").</summary>
    public string ApiKeyPrefix { get; set; } = "";

    /// <summary>Short status string. "ok" / "ok (retried)" on delivery, error class on failure.</summary>
    public string? Status { get; set; }

    public string? Error { get; set; }
}

public class SentRecordsSnapshot
{
    public List<SentRecordEntry> Records { get; set; } = new();
    public long TotalSent { get; set; }
    public long TotalDelivered { get; set; }
    public long TotalFailed { get; set; }
    public int BufferSize { get; set; }
    public int BufferUsed { get; set; }
}
