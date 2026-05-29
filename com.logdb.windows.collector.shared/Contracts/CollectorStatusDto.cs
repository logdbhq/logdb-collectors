namespace com.logdb.windows.collector.shared.Contracts;

public class CollectorStatusDto
{
    public string ServiceName { get; set; } = "LogDB Windows Collector";
    public CollectorInstanceMode InstanceMode { get; set; } = CollectorInstanceMode.Service;
    public string ControlPipeName { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public DateTime UtcNow { get; set; } = DateTime.UtcNow;
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public string ConfigPath { get; set; } = string.Empty;
    public List<ModuleStatusDto> Modules { get; set; } = new();
}

public class ModuleStatusDto
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string State { get; set; } = "Stopped";
    public DateTime? LastSuccessTimeUtc { get; set; }
    public string? LastError { get; set; }
    public long SentCount { get; set; }
    public long FailedCount { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class DiagnosticEntryDto
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Level { get; set; } = "Information";
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// The timestamp carried by the underlying log record (e.g. the EventLog
    /// record time or the IIS log-line time), as distinct from
    /// <see cref="TimestampUtc"/>, which is when the collector emitted this
    /// diagnostic line. Null for diagnostics that aren't about a specific
    /// collected record.
    /// </summary>
    public DateTime? EventTimestampUtc { get; set; }
}
