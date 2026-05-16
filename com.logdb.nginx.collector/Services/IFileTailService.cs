using com.logdb.nginx.collector.Models;

namespace com.logdb.nginx.collector.Services;

public interface IFileTailService
{
    IReadOnlyList<TailTarget> GetTargets();
    PipelineStatus GetPipelineStatus();
    /// <summary>
    /// Tails all configured log files. Returns true if any new data was read.
    /// </summary>
    Task<bool> TailAsync(CancellationToken cancellationToken = default);
    List<NginxLogRecord> ReadRecentLines(int maxLines = 200);
}
