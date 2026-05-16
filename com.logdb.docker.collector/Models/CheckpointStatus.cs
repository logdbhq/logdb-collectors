namespace com.logdb.docker.collector.Models;

public class CheckpointStatus
{
    public bool Enabled { get; set; }
    public string CheckpointFilePath { get; set; } = "";
    public int LoadedCount { get; set; }
    public DateTime? LastFlushUtc { get; set; }
    public string? Error { get; set; }
}
