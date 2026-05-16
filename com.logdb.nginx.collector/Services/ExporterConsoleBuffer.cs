using com.logdb.nginx.collector.Models;

namespace com.logdb.nginx.collector.Services;

public class ExporterConsoleBuffer
{
    private readonly object _lock = new();
    private readonly ExporterCallEntry?[] _buffer;
    private int _head;
    private int _count;
    private long _totalCalls;
    private long _nextId;

    public ExporterConsoleBuffer(int capacity = 500)
    {
        _buffer = new ExporterCallEntry[capacity];
    }

    public void Record(ExporterCallEntry entry)
    {
        lock (_lock)
        {
            entry.Id = ++_nextId;
            _buffer[_head] = entry;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
            _totalCalls++;
        }
    }

    public ExporterConsoleSnapshot GetRecent(int maxCount = 200, string? outcome = null)
    {
        lock (_lock)
        {
            var start = (_head - _count + _buffer.Length) % _buffer.Length;
            var items = new List<ExporterCallEntry>(_count);

            for (int i = 0; i < _count; i++)
            {
                var entry = _buffer[(start + i) % _buffer.Length];
                if (entry is null) continue;

                if (outcome is not null && !entry.Outcome.Equals(outcome, StringComparison.OrdinalIgnoreCase))
                    continue;

                items.Add(entry);
            }

            var result = items.Count > maxCount
                ? items.Skip(items.Count - maxCount).ToList()
                : items;

            return new ExporterConsoleSnapshot
            {
                Calls = result,
                TotalCalls = _totalCalls,
                BufferSize = _buffer.Length,
                BufferUsed = _count
            };
        }
    }
}
