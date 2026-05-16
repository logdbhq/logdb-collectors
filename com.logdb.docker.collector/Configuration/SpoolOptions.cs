namespace com.logdb.docker.collector.Configuration;

public class SpoolOptions
{
    public const string Section = "Spool";

    public bool Enabled { get; set; } = true;
    public string DirectoryPath { get; set; } = "spool";
    public long MaxDiskBytes { get; set; } = 2_147_483_648; // 2 GB
    public long MaxSegmentBytes { get; set; } = 10_485_760; // 10 MB
    public int FlushIntervalSeconds { get; set; } = 5;
    public int ReplayBatchSize { get; set; } = 50_000;
    public OverflowPolicy WhenFull { get; set; } = OverflowPolicy.DropOldest;
}

public enum OverflowPolicy
{
    DropOldest,
    DropNewest,
    RejectWrites
}
