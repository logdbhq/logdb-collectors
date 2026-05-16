namespace com.logdb.docker.collector.Models;

public class FileCheckpoint
{
    public string FilePath { get; set; } = "";
    public string ContainerId { get; set; } = "";
    public string ContainerName { get; set; } = "";
    public long Offset { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
}
