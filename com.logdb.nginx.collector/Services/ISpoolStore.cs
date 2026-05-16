using com.logdb.nginx.collector.Models;

namespace com.logdb.nginx.collector.Services;

public interface ISpoolStore
{
    void Initialize();
    void Append(NginxLogRecord record);
    void AppendBatch(IReadOnlyList<NginxLogRecord> records);
    List<NginxLogRecord> ReadBatch(int maxCount);
    void CommitBatch(int count);
    void EnforceLimit();
    void SetMaxDiskBytes(long maxBytes);
    SpoolStatus GetStatus();
    void Clear();
}
