namespace com.logdb.docker.collector.Models;

/// <summary>
/// One time-bucket of exporter activity: how many records the exporter handed to
/// grpc-logger in this window, split by delivery outcome, plus batch counts.
/// Container metrics are sent on a separate cadence and tracked separately.
/// Feeds the Activity chart.
/// </summary>
public class ActivityBucket
{
    /// <summary>Start of the bucket window (UTC).</summary>
    public DateTime StartUtc { get; set; }

    /// <summary>Log records whose batch flushed to grpc-logger without error.</summary>
    public long Delivered { get; set; }

    /// <summary>Records whose batch failed to send (logs or metrics).</summary>
    public long Failed { get; set; }

    /// <summary>Log send (batch) attempts that fell in this window.</summary>
    public long Batches { get; set; }

    /// <summary>Container-metric records delivered to grpc-logger.</summary>
    public long MetricsRecords { get; set; }

    /// <summary>Metric send (batch) attempts that fell in this window.</summary>
    public long MetricsBatches { get; set; }
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
    public long TotalBatches { get; set; }
    public long TotalMetricsRecords { get; set; }
    public long TotalMetricsBatches { get; set; }

    public DateTime GeneratedUtc { get; set; }
}
