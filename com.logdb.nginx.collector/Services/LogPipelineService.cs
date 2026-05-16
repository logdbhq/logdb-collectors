using com.logdb.nginx.collector.Models;

namespace com.logdb.nginx.collector.Services;

public class LogPipelineService : ILogRecordSink
{
    private readonly ISpoolStore _spool;
    private readonly LiveConsoleBuffer _consoleBuffer;

    public LogPipelineService(ISpoolStore spool, LiveConsoleBuffer consoleBuffer)
    {
        _spool = spool;
        _consoleBuffer = consoleBuffer;
    }

    public void Write(NginxLogRecord record)
    {
        _consoleBuffer.Add(record);
        _spool.Append(record);
    }

    public void WriteBatch(IReadOnlyList<NginxLogRecord> records)
    {
        _consoleBuffer.AddBatch(records);
        _spool.AppendBatch(records);
    }
}
