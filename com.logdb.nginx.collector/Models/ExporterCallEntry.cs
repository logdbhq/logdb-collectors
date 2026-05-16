namespace com.logdb.nginx.collector.Models;

public class ExporterCallEntry
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }

    /// <summary>"success", "skipped", "failed".</summary>
    public string Outcome { get; set; } = "";

    /// <summary>Number of raw nginx records in the batch (before aggregation).</summary>
    public int RecordCount { get; set; }

    /// <summary>Number of LogDB events sent (after access-log aggregation).</summary>
    public int EventCount { get; set; }

    public string Endpoint { get; set; } = "";

    /// <summary>First 8 chars of the API key (or "(empty)" / "(placeholder)") for sanity-check without leaking the full key.</summary>
    public string ApiKeyPrefix { get; set; } = "";

    public double DurationMs { get; set; }

    /// <summary>Short reason / status string. For success: "ok" or status code. For failure: error class.</summary>
    public string? Status { get; set; }

    public string? Error { get; set; }
}

public class ExporterConsoleSnapshot
{
    public List<ExporterCallEntry> Calls { get; set; } = new();
    public long TotalCalls { get; set; }
    public int BufferSize { get; set; }
    public int BufferUsed { get; set; }
}
