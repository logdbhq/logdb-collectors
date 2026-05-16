namespace com.logdb.windows.collector.shared.Contracts;

public sealed class ValidationIssueDto
{
    public string Code { get; set; } = string.Empty;
    public string Severity { get; set; } = "Error";
    public string Message { get; set; } = string.Empty;
    public string? Source { get; set; }
}

public sealed class ValidationResultDto
{
    public bool Success { get; set; }
    public string Code { get; set; } = "OK";
    public string Message { get; set; } = string.Empty;
    public List<ValidationIssueDto> Issues { get; set; } = new();
}

public sealed class PreviewRequestDto
{
    public int Max { get; set; } = 20;
}

public sealed class PreviewResultDto<T>
{
    public bool Success { get; set; }
    public string Code { get; set; } = "OK";
    public string Message { get; set; } = string.Empty;
    public List<T> Rows { get; set; } = new();
}

public sealed class EventLogPreviewRowDto
{
    public DateTime TimeUtc { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int EventId { get; set; }
    public string MessageSnippet { get; set; } = string.Empty;
}

public sealed class IisPreviewRowDto
{
    public DateTime? TimeUtc { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Uri { get; set; } = string.Empty;
    public int? Status { get; set; }
    public long? TimeTakenMs { get; set; }
    public string ClientIp { get; set; } = string.Empty;
}

public sealed class MetricPreviewRowDto
{
    public string Metric { get; set; } = string.Empty;
    public double Value { get; set; }
    public string Unit { get; set; } = string.Empty;
    public Dictionary<string, string> Tags { get; set; } = new();
}
