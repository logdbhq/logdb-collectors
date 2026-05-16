namespace com.logdb.docker.collector.Models;

public class PipelineStatus
{
    public int ActiveTargets { get; set; }
    public long RecordsRead { get; set; }
    public long ParseErrors { get; set; }
    public long ReadErrors { get; set; }
    public DateTime? LastRecordTimestamp { get; set; }
    public Dictionary<string, FileOffsetInfo> FileOffsets { get; set; } = new();
}

public class FileOffsetInfo
{
    public string ContainerName { get; set; } = "";
    public long Offset { get; set; }
    public DateTime? LastReadUtc { get; set; }
}
