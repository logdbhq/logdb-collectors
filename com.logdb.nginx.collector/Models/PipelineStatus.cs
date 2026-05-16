namespace com.logdb.nginx.collector.Models;

public class PipelineStatus
{
    public int ActiveTargets { get; set; }
    public int ActiveFiles { get; set; }
    public long AccessRecordsRead { get; set; }
    public long ErrorRecordsRead { get; set; }
    public long ParseErrors { get; set; }
    public long ReadErrors { get; set; }
    public long FilteredStaticFiles { get; set; }
    public long FilteredByRules { get; set; }
    public long RotationsDetected { get; set; }
    public DateTime? LastRecordTimestamp { get; set; }
    public DateTime? LastTailCycleUtc { get; set; }
}
