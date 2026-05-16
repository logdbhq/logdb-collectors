using com.logdb.docker.collector.Models;

namespace com.logdb.docker.collector.Services;

public interface IFileTailService
{
    IReadOnlyList<TailTarget> GetTargets();
    PipelineStatus GetPipelineStatus();
    /// <summary>
    /// Tails all discovered container log files. Returns true if any new data was read.
    /// </summary>
    Task<bool> TailAsync(CancellationToken cancellationToken = default);
    LogSizeEstimate EstimateSize(string logPath, DateTime fromUtc);
    void ResetOffsets();
    void ResetOffset(string containerId);
}

public class LogSizeEstimate
{
    public string LogPath { get; set; } = "";
    public long TotalFileBytes { get; set; }
    public long EstimatedExportBytes { get; set; }
    public long EstimatedLineCount { get; set; }
    public DateTime? EarliestTimestamp { get; set; }
    public DateTime? LatestTimestamp { get; set; }
}
