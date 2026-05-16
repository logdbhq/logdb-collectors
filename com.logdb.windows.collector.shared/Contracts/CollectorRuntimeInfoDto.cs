namespace com.logdb.windows.collector.shared.Contracts;

public sealed class CollectorRuntimeInfoDto
{
    public CollectorInstanceMode Mode { get; set; } = CollectorInstanceMode.Console;
    public string PipeName { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public string ConfigPath { get; set; } = string.Empty;
}
