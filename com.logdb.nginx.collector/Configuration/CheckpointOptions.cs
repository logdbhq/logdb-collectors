namespace com.logdb.nginx.collector.Configuration;

public class CheckpointOptions
{
    public const string Section = "Checkpoint";

    public bool Enabled { get; set; } = true;
    public string FilePath { get; set; } = "checkpoints.json";
    public int FlushIntervalSeconds { get; set; } = 15;
}
