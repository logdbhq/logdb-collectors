using com.logdb.docker.collector.Models;

namespace com.logdb.docker.collector.Services;

public class LogPipelineService : ILogRecordSink
{
    private readonly ISpoolStore _spool;
    private readonly ContainerToggleService _toggleService;
    private readonly FilterRuleService _filterService;
    private readonly LiveConsoleBuffer _consoleBuffer;

    public LogPipelineService(ISpoolStore spool, ContainerToggleService toggleService,
        FilterRuleService filterService, LiveConsoleBuffer consoleBuffer)
    {
        _spool = spool;
        _toggleService = toggleService;
        _filterService = filterService;
        _consoleBuffer = consoleBuffer;
    }

    public void Write(LogRecord record)
    {
        // Always capture to live console buffer (even if filtered from export)
        _consoleBuffer.Add(record);

        if (!_toggleService.ShouldExport(record.ContainerId, record.Stream, record.ParsedLevel))
            return;

        if (_filterService.ShouldExclude(record))
            return;

        _spool.Append(record);
    }

    public void WriteBatch(IReadOnlyList<LogRecord> records)
    {
        _consoleBuffer.AddBatch(records);

        var exportable = new List<LogRecord>(records.Count);
        foreach (var record in records)
        {
            if (!_toggleService.ShouldExport(record.ContainerId, record.Stream, record.ParsedLevel))
                continue;

            if (_filterService.ShouldExclude(record))
                continue;

            exportable.Add(record);
        }

        if (exportable.Count > 0)
            _spool.AppendBatch(exportable);
    }
}
