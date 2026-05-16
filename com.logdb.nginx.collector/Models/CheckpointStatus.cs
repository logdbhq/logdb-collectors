namespace com.logdb.nginx.collector.Models;

public class CheckpointStatus
{
    public bool Enabled { get; set; }
    public int TrackedFiles { get; set; }
    public DateTime? LastFlushUtc { get; set; }
    public string FilePath { get; set; } = "";
}
