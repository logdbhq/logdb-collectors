using com.logdb.nginx.collector.Models;

namespace com.logdb.nginx.collector.Services;

public interface ILogRecordSink
{
    void Write(NginxLogRecord record);
    void WriteBatch(IReadOnlyList<NginxLogRecord> records);
}
