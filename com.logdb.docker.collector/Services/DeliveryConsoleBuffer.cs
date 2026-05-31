using com.logdb.docker.collector.Models;

namespace com.logdb.docker.collector.Services;

/// <summary>
/// Ring buffer of the most recent records the exporter handed to grpc-logger,
/// one entry per sent event (after aggregation for logs), with full detail and
/// per-record delivery outcome. Covers both log and metric records so the UI can
/// answer "did THIS specific record actually leave the box?". The per-record
/// counterpart to the batch-level stats on the Exporter card and the time-series
/// on the Activity chart.
/// </summary>
public class DeliveryConsoleBuffer
{
    private readonly object _lock = new();
    private readonly SentRecordEntry?[] _buffer;
    private int _head;
    private int _count;
    private long _totalSent;
    private long _totalDelivered;
    private long _totalFailed;
    private long _totalLogs;
    private long _totalMetrics;
    private long _nextId;

    public DeliveryConsoleBuffer(int capacity = 1000)
    {
        _buffer = new SentRecordEntry[capacity];
    }

    public void Record(SentRecordEntry entry)
    {
        lock (_lock)
        {
            entry.Id = ++_nextId;
            _buffer[_head] = entry;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
            _totalSent++;
            if (entry.Outcome.Equals("delivered", StringComparison.OrdinalIgnoreCase)) _totalDelivered++;
            else if (entry.Outcome.Equals("failed", StringComparison.OrdinalIgnoreCase)) _totalFailed++;
            if (entry.Kind.Equals("metric", StringComparison.OrdinalIgnoreCase)) _totalMetrics++;
            else _totalLogs++;
        }
    }

    public void RecordBatch(IEnumerable<SentRecordEntry> entries)
    {
        foreach (var entry in entries)
            Record(entry);
    }

    public SentRecordsSnapshot GetRecent(int maxCount = 200, string? outcome = null, string? kind = null, string? filter = null)
    {
        lock (_lock)
        {
            var start = (_head - _count + _buffer.Length) % _buffer.Length;
            var items = new List<SentRecordEntry>(_count);

            for (int i = 0; i < _count; i++)
            {
                var entry = _buffer[(start + i) % _buffer.Length];
                if (entry is null) continue;

                if (outcome is not null && !entry.Outcome.Equals(outcome, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (kind is not null && !entry.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (filter is not null && !Matches(entry, filter))
                    continue;

                items.Add(entry);
            }

            var result = items.Count > maxCount
                ? items.Skip(items.Count - maxCount).ToList()
                : items;

            return new SentRecordsSnapshot
            {
                Records = result,
                TotalSent = _totalSent,
                TotalDelivered = _totalDelivered,
                TotalFailed = _totalFailed,
                TotalLogs = _totalLogs,
                TotalMetrics = _totalMetrics,
                BufferSize = _buffer.Length,
                BufferUsed = _count
            };
        }
    }

    private static bool Matches(SentRecordEntry e, string filter)
    {
        return e.Container?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true
            || e.ComposeService?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true
            || e.Message?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true
            || e.Category?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true
            || e.Level?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true
            || e.Image?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true
            || e.Guid?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;
    }
}
