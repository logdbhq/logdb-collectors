using com.logdb.windows.collector.shared.Contracts;

namespace com.logdb.windows.collector.Diagnostics;

public sealed class CollectorLogSink
{
    private readonly string _logDirectory;
    private readonly int _capacity;
    private readonly object _sync = new();
    private readonly Queue<DiagnosticEntryDto> _entries = new();
    private bool _fileLoggingEnabled = true;
    private string? _fileLoggingFailureMessage;
    private bool _fileLoggingFailureReported;

    public CollectorLogSink(string logDirectory, int capacity)
    {
        _logDirectory = logDirectory;
        _capacity = Math.Max(100, capacity);

        try
        {
            Directory.CreateDirectory(_logDirectory);
        }
        catch (Exception ex)
        {
            _fileLoggingEnabled = false;
            _fileLoggingFailureMessage = BuildFileLoggingFailureMessage(ex);
        }
    }

    public void Write(LogLevel level, string category, string message, DateTime? eventTimestampUtc = null)
    {
        var timestamp = DateTime.UtcNow;
        var entry = new DiagnosticEntryDto
        {
            TimestampUtc = timestamp,
            Level = level.ToString(),
            Category = category,
            Message = message,
            EventTimestampUtc = eventTimestampUtc
        };

        var line = $"[{timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] [{entry.Level}] [{entry.Category}] {entry.Message}";
        var filePath = Path.Combine(_logDirectory, $"collector-{timestamp:yyyyMMdd}.log");

        lock (_sync)
        {
            Enqueue(entry);

            if (!_fileLoggingEnabled)
            {
                ReportFileLoggingFailureIfNeeded();
                return;
            }

            try
            {
                File.AppendAllText(filePath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                _fileLoggingEnabled = false;
                _fileLoggingFailureMessage = BuildFileLoggingFailureMessage(ex);
                ReportFileLoggingFailureIfNeeded();
            }
        }
    }

    public IReadOnlyList<DiagnosticEntryDto> GetRecent(int maxEntries)
    {
        lock (_sync)
        {
            return _entries
                .TakeLast(Math.Max(1, maxEntries))
                .ToList();
        }
    }

    private void Enqueue(DiagnosticEntryDto entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > _capacity)
        {
            _entries.Dequeue();
        }
    }

    private void ReportFileLoggingFailureIfNeeded()
    {
        if (_fileLoggingFailureReported || string.IsNullOrWhiteSpace(_fileLoggingFailureMessage))
        {
            return;
        }

        Enqueue(new DiagnosticEntryDto
        {
            TimestampUtc = DateTime.UtcNow,
            Level = LogLevel.Warning.ToString(),
            Category = nameof(CollectorLogSink),
            Message = _fileLoggingFailureMessage
        });

        _fileLoggingFailureReported = true;
    }

    private string BuildFileLoggingFailureMessage(Exception ex)
    {
        return $"File logging disabled for '{_logDirectory}': {ex.Message}";
    }
}
