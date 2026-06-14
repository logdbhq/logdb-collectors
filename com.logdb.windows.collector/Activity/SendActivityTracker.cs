using System.Text.Json;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.shared.Services;

namespace com.logdb.windows.collector.Activity;

/// <summary>
/// Persisted, hourly-bucketed record of how many records were sent (and failed)
/// per module / collection / host. Backs the admin UI's Throughput charts.
///
/// In-memory writes are cheap (a dictionary keyed by hour+dimensions); the store
/// is flushed to <c>send-activity.json</c> atomically at most once per
/// <see cref="FlushInterval"/> and on <see cref="Flush"/> (shutdown). Buckets
/// older than <see cref="Retention"/> are pruned. Hourly granularity is the raw
/// resolution; day granularity is rolled up at query time.
/// </summary>
public sealed class SendActivityTracker : ISendActivitySink
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<BucketKey, long[]> _buckets = new(); // value = [sent, failed]

    private bool _dirty;
    private DateTime _lastFlushUtc = DateTime.MinValue;

    public SendActivityTracker(string? path = null)
    {
        _path = path ?? CollectorPathDefaults.SendActivityPath;
        Load();
    }

    private readonly record struct BucketKey(long HourTicks, string Module, string Collection, string Host);

    public void Record(string module, string? collection, string? host, long records, bool success, DateTime whenUtc)
    {
        if (records <= 0) return;
        if (whenUtc.Kind != DateTimeKind.Utc) whenUtc = whenUtc.ToUniversalTime();
        var hourTicks = whenUtc.Ticks - (whenUtc.Ticks % TimeSpan.TicksPerHour);
        var key = new BucketKey(hourTicks, Norm(module), Norm(collection), Norm(host));

        bool flushDue;
        lock (_lock)
        {
            if (!_buckets.TryGetValue(key, out var c)) { c = new long[2]; _buckets[key] = c; }
            if (success) c[0] += records; else c[1] += records;
            _dirty = true;
            PruneLocked(whenUtc);
            flushDue = (whenUtc - _lastFlushUtc) >= FlushInterval;
        }

        if (flushDue) Flush();
    }

    /// <summary>Aggregate the stored buckets into chart series per the query.</summary>
    public SendActivityDto GetActivity(SendActivityQueryDto q)
    {
        var fromUtc = AsUtc(q.FromUtc);
        var toUtc = AsUtc(q.ToUtc);
        if (toUtc < fromUtc) (fromUtc, toUtc) = (toUtc, fromUtc);

        var modules = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var hosts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var collections = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        // group name -> (snapped bucket ticks -> [sent, failed])
        var grouped = new Dictionary<string, Dictionary<long, long[]>>(StringComparer.OrdinalIgnoreCase);
        long totalSent = 0, totalFailed = 0;

        lock (_lock)
        {
            foreach (var (key, c) in _buckets)
            {
                // Distinct values for the UI filter dropdowns (from the whole store).
                if (key.Module.Length > 0) modules.Add(key.Module);
                if (key.Host.Length > 0) hosts.Add(key.Host);
                if (key.Collection.Length > 0) collections.Add(key.Collection);

                var bucketStart = new DateTime(key.HourTicks, DateTimeKind.Utc);
                if (bucketStart < fromUtc || bucketStart >= toUtc) continue;
                if (!Match(q.Module, key.Module) || !Match(q.Host, key.Host) || !Match(q.Collection, key.Collection)) continue;

                var groupName = q.GroupBy switch
                {
                    SendActivityGroupBy.Module => key.Module,
                    SendActivityGroupBy.Host => key.Host,
                    SendActivityGroupBy.Collection => key.Collection,
                    _ => "All"
                };
                if (groupName.Length == 0) groupName = "(none)";

                var slot = SnapTicks(key.HourTicks, q.Granularity);
                if (!grouped.TryGetValue(groupName, out var slots)) { slots = new(); grouped[groupName] = slots; }
                if (!slots.TryGetValue(slot, out var agg)) { agg = new long[2]; slots[slot] = agg; }
                agg[0] += c[0]; agg[1] += c[1];
                totalSent += c[0]; totalFailed += c[1];
            }
        }

        // Aligned X grid so all series share buckets (and gaps render as zero).
        var grid = BuildGrid(fromUtc, toUtc, q.Granularity);
        var series = new List<SendActivitySeriesDto>();
        foreach (var (name, slots) in grouped.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var s = new SendActivitySeriesDto { Name = name };
            foreach (var t in grid)
            {
                slots.TryGetValue(t, out var agg);
                s.Buckets.Add(new SendActivityBucketDto
                {
                    StartUtc = new DateTime(t, DateTimeKind.Utc),
                    Sent = agg?[0] ?? 0,
                    Failed = agg?[1] ?? 0
                });
            }
            series.Add(s);
        }

        return new SendActivityDto
        {
            Series = series,
            TotalSent = totalSent,
            TotalFailed = totalFailed,
            Modules = modules.ToList(),
            Hosts = hosts.ToList(),
            Collections = collections.ToList(),
            GeneratedUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Lifetime (within retention) records sent / failed per module — the real
    /// numbers behind the Modules grid's "Sent" column.
    /// </summary>
    public IReadOnlyDictionary<string, (long Sent, long Failed)> GetTotalsByModule()
    {
        var result = new Dictionary<string, (long Sent, long Failed)>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (var (key, c) in _buckets)
            {
                var m = key.Module.Length == 0 ? "(none)" : key.Module;
                result.TryGetValue(m, out var t);
                result[m] = (t.Sent + c[0], t.Failed + c[1]);
            }
        }
        return result;
    }

    /// <summary>
    /// Drops every recorded bucket and clears the persisted store — zeroes the
    /// Modules grid "Sent"/"Failed" totals and the Throughput history. Invoked
    /// from the admin UI; sending itself is unaffected and starts re-counting
    /// from zero on the next batch.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _buckets.Clear();
            _dirty = false;
            _lastFlushUtc = DateTime.UtcNow;
        }

        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch
        {
            // If the file can't be deleted, an empty in-memory store will still
            // overwrite it on the next flush. Don't let cleanup throw.
        }
    }

    public void Flush()
    {
        List<PersistRow> rows;
        lock (_lock)
        {
            if (!_dirty) return;
            rows = _buckets.Select(kv => new PersistRow
            {
                HourTicks = kv.Key.HourTicks,
                Module = kv.Key.Module,
                Collection = kv.Key.Collection,
                Host = kv.Key.Host,
                Sent = kv.Value[0],
                Failed = kv.Value[1]
            }).ToList();
            _dirty = false;
            _lastFlushUtc = DateTime.UtcNow;
        }

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(rows);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Best-effort: a failed flush must never break sending. Mark dirty so
            // the next interval retries.
            lock (_lock) { _dirty = true; }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var rows = JsonSerializer.Deserialize<List<PersistRow>>(File.ReadAllText(_path));
            if (rows == null) return;
            var cutoff = DateTime.UtcNow - Retention;
            lock (_lock)
            {
                foreach (var r in rows)
                {
                    if (new DateTime(r.HourTicks, DateTimeKind.Utc) < cutoff) continue;
                    var key = new BucketKey(r.HourTicks, Norm(r.Module), Norm(r.Collection), Norm(r.Host));
                    if (!_buckets.TryGetValue(key, out var c)) { c = new long[2]; _buckets[key] = c; }
                    c[0] += r.Sent; c[1] += r.Failed;
                }
            }
        }
        catch
        {
            // Corrupt/unreadable store → start empty rather than crash.
        }
    }

    // Caller holds _lock. Drops buckets older than the retention window.
    private void PruneLocked(DateTime nowUtc)
    {
        var cutoffTicks = (nowUtc - Retention).Ticks;
        if (_buckets.Count == 0) return;
        List<BucketKey>? stale = null;
        foreach (var key in _buckets.Keys)
            if (key.HourTicks < cutoffTicks) (stale ??= new()).Add(key);
        if (stale != null) foreach (var k in stale) _buckets.Remove(k);
    }

    private static bool Match(string? filter, string value) =>
        string.IsNullOrEmpty(filter) || string.Equals(filter, value, StringComparison.OrdinalIgnoreCase);

    private static string Norm(string? s) => (s ?? string.Empty).Trim();

    private static DateTime AsUtc(DateTime dt) =>
        dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt.ToUniversalTime(), DateTimeKind.Utc);

    private static long SnapTicks(long hourTicks, SendActivityGranularity g) =>
        g == SendActivityGranularity.Day ? hourTicks - (hourTicks % TimeSpan.TicksPerDay) : hourTicks;

    private static List<long> BuildGrid(DateTime fromUtc, DateTime toUtc, SendActivityGranularity g)
    {
        var step = g == SendActivityGranularity.Day ? TimeSpan.TicksPerDay : TimeSpan.TicksPerHour;
        var start = fromUtc.Ticks - (fromUtc.Ticks % step);
        var end = toUtc.Ticks;
        var grid = new List<long>();
        for (var t = start; t < end && grid.Count < 100_000; t += step) grid.Add(t);
        return grid;
    }

    private sealed class PersistRow
    {
        public long HourTicks { get; set; }
        public string Module { get; set; } = string.Empty;
        public string Collection { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public long Sent { get; set; }
        public long Failed { get; set; }
    }
}
