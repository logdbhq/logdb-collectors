using com.logdb.nginx.collector.Models;

namespace com.logdb.nginx.collector.Services;

public class LiveConsoleBuffer
{
    private readonly object _lock = new();
    private readonly NginxLogRecord?[] _buffer;
    private int _head;
    private int _count;
    private long _totalIngested;

    public LiveConsoleBuffer(int capacity = 500)
    {
        _buffer = new NginxLogRecord[capacity];
    }

    private const int MaxMessageLength = 512;

    public void Add(NginxLogRecord record)
    {
        if (record.Message is not null && record.Message.Length > MaxMessageLength)
        {
            record = new NginxLogRecord
            {
                Timestamp = record.Timestamp,
                Message = record.Message[..MaxMessageLength] + "...",
                LogType = record.LogType,
                TargetName = record.TargetName,
                HostName = record.HostName,
                SourceFile = record.SourceFile,
                RemoteAddress = record.RemoteAddress,
                Method = record.Method,
                Path = record.Path,
                Protocol = record.Protocol,
                StatusCode = record.StatusCode,
                ResponseBytes = record.ResponseBytes,
                UserAgent = record.UserAgent,
                Severity = record.Severity
            };
        }

        lock (_lock)
        {
            _buffer[_head] = record;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
            _totalIngested++;
        }
    }

    public void AddBatch(IReadOnlyList<NginxLogRecord> records)
    {
        foreach (var record in records)
            Add(record);
    }

    public LiveConsoleSnapshot GetRecent(int maxCount = 100, string? filter = null)
    {
        lock (_lock)
        {
            var start = (_head - _count + _buffer.Length) % _buffer.Length;
            var items = new List<LiveConsoleEntry>(_count);

            for (int i = 0; i < _count; i++)
            {
                var record = _buffer[(start + i) % _buffer.Length];
                if (record is null) continue;

                if (filter is not null &&
                    !(record.TargetName?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true ||
                      record.Path?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true ||
                      record.Message?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true))
                    continue;

                items.Add(new LiveConsoleEntry
                {
                    Timestamp = record.Timestamp,
                    LogType = record.LogType.ToString().ToLowerInvariant(),
                    Target = record.TargetName ?? string.Empty,
                    Method = record.Method,
                    Path = record.Path,
                    StatusCode = record.StatusCode,
                    ResponseBytes = record.ResponseBytes,
                    RemoteAddress = record.RemoteAddress,
                    Message = record.Message ?? "",
                    Severity = record.Severity
                });
            }

            var result = items.Count > maxCount
                ? items.Skip(items.Count - maxCount).ToList()
                : items;

            return new LiveConsoleSnapshot
            {
                Records = result,
                TotalIngested = _totalIngested,
                BufferSize = _buffer.Length,
                BufferUsed = _count
            };
        }
    }
}

public class LiveConsoleEntry
{
    public DateTime Timestamp { get; set; }
    public string LogType { get; set; } = "";
    public string Target { get; set; } = "";
    public string? Method { get; set; }
    public string? Path { get; set; }
    public int? StatusCode { get; set; }
    public long? ResponseBytes { get; set; }
    public string? RemoteAddress { get; set; }
    public string? Message { get; set; }
    public string? Severity { get; set; }
}

public class LiveConsoleSnapshot
{
    public List<LiveConsoleEntry> Records { get; set; } = new();
    public long TotalIngested { get; set; }
    public int BufferSize { get; set; }
    public int BufferUsed { get; set; }
}
