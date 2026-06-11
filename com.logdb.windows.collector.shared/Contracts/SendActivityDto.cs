namespace com.logdb.windows.collector.shared.Contracts;

/// <summary>Time-bucket size for a send-activity query.</summary>
public enum SendActivityGranularity
{
    Hour = 0,
    Day = 1
}

/// <summary>Which dimension the returned series are split by.</summary>
public enum SendActivityGroupBy
{
    None = 0,
    Module = 1,
    Host = 2,
    Collection = 3
}

/// <summary>
/// Request for throughput data over a date range. Null/empty filters mean "all".
/// </summary>
public sealed class SendActivityQueryDto
{
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public SendActivityGranularity Granularity { get; set; } = SendActivityGranularity.Hour;
    public SendActivityGroupBy GroupBy { get; set; } = SendActivityGroupBy.Module;

    // Optional filters to narrow the data before grouping.
    public string? Module { get; set; }
    public string? Host { get; set; }
    public string? Collection { get; set; }
}

/// <summary>One time bucket: records sent vs failed within [StartUtc, StartUtc+granularity).</summary>
public sealed class SendActivityBucketDto
{
    public DateTime StartUtc { get; set; }
    public long Sent { get; set; }
    public long Failed { get; set; }
}

/// <summary>A single chart series (one value of the GroupBy dimension, e.g. "EventLog").</summary>
public sealed class SendActivitySeriesDto
{
    public string Name { get; set; } = string.Empty;
    public List<SendActivityBucketDto> Buckets { get; set; } = new();
}

/// <summary>
/// Throughput response: one series per GroupBy value, each with aligned time
/// buckets, plus the distinct dimension values available (to populate filters).
/// </summary>
public sealed class SendActivityDto
{
    public List<SendActivitySeriesDto> Series { get; set; } = new();

    public long TotalSent { get; set; }
    public long TotalFailed { get; set; }

    // Distinct values present in the store, for the UI's filter dropdowns.
    public List<string> Modules { get; set; } = new();
    public List<string> Hosts { get; set; } = new();
    public List<string> Collections { get; set; } = new();

    public DateTime GeneratedUtc { get; set; }
}
