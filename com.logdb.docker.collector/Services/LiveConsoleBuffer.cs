using com.logdb.docker.collector.Models;

namespace com.logdb.docker.collector.Services;

public class LiveConsoleBuffer
{
    private readonly object _lock = new();
    private readonly LogRecord[] _buffer;
    private int _head;
    private int _count;
    private long _totalIngested;

    public LiveConsoleBuffer(int capacity = 500)
    {
        _buffer = new LogRecord[capacity];
    }

    private const int MaxMessageLength = 512;

    public void Add(LogRecord record)
    {
        // Trim message for console buffer - full content goes to spool/export
        if (record.Message.Length > MaxMessageLength)
        {
            record = new LogRecord
            {
                Timestamp = record.Timestamp,
                Message = record.Message[..MaxMessageLength] + "...",
                Stream = record.Stream,
                ContainerId = record.ContainerId,
                ContainerName = record.ContainerName,
                Image = record.Image,
                HostName = record.HostName,
                SourceType = record.SourceType,
                Labels = record.Labels,
                ComposeProject = record.ComposeProject,
                ComposeService = record.ComposeService
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

    public void AddBatch(IReadOnlyList<LogRecord> records)
    {
        foreach (var record in records)
            Add(record);
    }

    public LiveConsoleSnapshot GetRecent(int maxCount = 100, string? containerFilter = null)
    {
        lock (_lock)
        {
            var start = (_head - _count + _buffer.Length) % _buffer.Length;
            var items = new List<LiveConsoleEntry>(_count);

            for (int i = 0; i < _count; i++)
            {
                var record = _buffer[(start + i) % _buffer.Length];
                if (containerFilter is not null &&
                    !record.ContainerName.Contains(containerFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                items.Add(new LiveConsoleEntry
                {
                    Timestamp = record.Timestamp,
                    ContainerId = record.ContainerId,
                    Container = record.ContainerName,
                    Image = record.Image,
                    Stream = record.Stream,
                    Message = record.Message,
                    Category = record.Category,
                    ParsedLevel = record.ParsedLevel,
                    ComposeService = record.ComposeService
                });
            }

            // Return the most recent maxCount entries
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
    public string ContainerId { get; set; } = "";
    public string Container { get; set; } = "";
    public string? Image { get; set; }
    public string Stream { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Category { get; set; }
    public string? ParsedLevel { get; set; }
    public string? ComposeService { get; set; }
    public bool Exported { get; set; }
}

public class LiveConsoleSnapshot
{
    public List<LiveConsoleEntry> Records { get; set; } = new();
    public long TotalIngested { get; set; }
    public int BufferSize { get; set; }
    public int BufferUsed { get; set; }
}
