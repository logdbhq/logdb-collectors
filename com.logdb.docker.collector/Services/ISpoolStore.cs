using com.logdb.docker.collector.Models;

namespace com.logdb.docker.collector.Services;

public interface ISpoolStore
{
    void Initialize();
    void Append(LogRecord record);
    void AppendBatch(IReadOnlyList<LogRecord> records);
    List<LogRecord> ReadBatch(int maxCount);
    void CommitBatch(int count);
    void EnforceLimit();
    void SetMaxDiskBytes(long maxBytes);
    SpoolStatus GetStatus();
    void Clear();
}
