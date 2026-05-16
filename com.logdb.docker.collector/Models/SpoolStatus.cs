namespace com.logdb.docker.collector.Models;

public class SpoolStatus
{
    public bool Enabled { get; set; }
    public string DirectoryPath { get; set; } = "";
    public long QueuedRecords { get; set; }
    public long DiskBytesUsed { get; set; }
    public long MaxDiskBytes { get; set; }
    public int UtilizationPercent { get; set; }
    public long ReplayedRecords { get; set; }
    public long DroppedRecords { get; set; }
    public DateTime? OldestRecordUtc { get; set; }
    public DateTime? LastWriteUtc { get; set; }
    public DateTime? LastReplayUtc { get; set; }
    public string? LastError { get; set; }
}
