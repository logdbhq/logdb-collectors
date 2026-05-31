namespace com.logdb.nginx.collector.Configuration;

public class SpoolOptions
{
    public const string Section = "Spool";

    public bool Enabled { get; set; } = true;
    public string DirectoryPath { get; set; } = "spool";
    public long MaxDiskBytes { get; set; } = 2_147_483_648; // 2 GB
    public long MaxSegmentBytes { get; set; } = 10_485_760; // 10 MB
    public int FlushIntervalSeconds { get; set; } = 5;
    public int ReplayBatchSize { get; set; } = 50_000;

    /// <summary>
    /// Max records handed to the exporter per send. A replay batch is sliced into
    /// chunks of this size and each chunk is committed as soon as it lands, so one
    /// oversized flush can't blow the send timeout and force the whole batch to be
    /// re-sent. Keeps forward progress through a backlog (e.g. an nginx error storm).
    /// </summary>
    public int SendChunkSize { get; set; } = 5_000;

    public OverflowPolicy WhenFull { get; set; } = OverflowPolicy.DropOldest;
}

public enum OverflowPolicy
{
    DropOldest,
    DropNewest,
    RejectWrites
}
