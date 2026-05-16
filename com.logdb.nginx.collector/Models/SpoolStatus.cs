namespace com.logdb.nginx.collector.Models;

public class SpoolStatus
{
    public bool Enabled { get; set; }
    public long QueuedRecords { get; set; }
    public long DiskBytesUsed { get; set; }
    public long MaxDiskBytes { get; set; }
    public double UtilizationPercent { get; set; }
    public long DroppedRecords { get; set; }
    public long ReplayedRecords { get; set; }
    public string? LastError { get; set; }
}
