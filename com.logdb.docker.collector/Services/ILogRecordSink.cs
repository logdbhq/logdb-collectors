using com.logdb.docker.collector.Models;

namespace com.logdb.docker.collector.Services;

public interface ILogRecordSink
{
    void Write(LogRecord record);
    void WriteBatch(IReadOnlyList<LogRecord> records);
}
