namespace com.logdb.nginx.collector.Models;

/// <summary>
/// One time-bucket of exporter activity: how many records the exporter handed to
/// grpc-logger in this window, split by delivery outcome, plus the number of send
/// (batch) attempts. Feeds the Activity chart.
/// </summary>
public class ActivityBucket
{
    /// <summary>Start of the bucket window (UTC).</summary>
    public DateTime StartUtc { get; set; }

    /// <summary>Records whose batch flushed to grpc-logger without error.</summary>
    public long Delivered { get; set; }

    /// <summary>Records whose batch failed to send.</summary>
    public long Failed { get; set; }

    /// <summary>Records skipped before sending (e.g. API key not configured).</summary>
    public long Skipped { get; set; }

    /// <summary>Send (batch) attempts that fell in this window.</summary>
    public long Batches { get; set; }
}

public class ActivitySnapshot
{
    public List<ActivityBucket> Buckets { get; set; } = new();

    /// <summary>Width of each bucket in seconds (granularity for the requested range).</summary>
    public int BucketSeconds { get; set; }

    /// <summary>The range the buckets span, in minutes.</summary>
    public int RangeMinutes { get; set; }

    // Cumulative-since-start totals (not just the window).
    public long TotalDelivered { get; set; }
    public long TotalFailed { get; set; }
    public long TotalSkipped { get; set; }
    public long TotalBatches { get; set; }

    public DateTime GeneratedUtc { get; set; }
}
