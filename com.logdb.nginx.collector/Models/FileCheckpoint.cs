namespace com.logdb.nginx.collector.Models;

public class FileCheckpoint
{
    public string FilePath { get; set; } = "";
    public string TargetName { get; set; } = "";
    public long Offset { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }

    /// <summary>
    /// File size at last checkpoint update. Used to detect copytruncate rotation.
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// Creation time of the file at last checkpoint. When the file at the
    /// same path has a newer creation time, it was replaced (rename/create rotation).
    /// </summary>
    public DateTime? FileCreatedUtc { get; set; }
}
