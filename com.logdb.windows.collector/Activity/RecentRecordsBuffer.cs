using System.Text.Json;
using com.logdb.windows.collector.shared.Contracts;
using LogDB.Client.Models;

namespace com.logdb.windows.collector.Activity;

/// <summary>
/// Small, in-memory ring buffer of the most recent records the collector handed
/// to the server, captured at the <see cref="Services.RecordingLogDbClient"/>
/// boundary so it's uniform across modules. Backs the admin UI's "Recent records"
/// view: unlike <see cref="SendActivityTracker"/> (which keeps only counts), this
/// keeps the serialized document body so a user can inspect exactly what was sent.
///
/// Deliberately bounded and non-persisted: holds at most <see cref="Capacity"/>
/// records (newest first) and is cleared on restart. Capture is best-effort and
/// must never affect delivery.
/// </summary>
public sealed class RecentRecordsBuffer
{
    public const int Capacity = 200;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly LinkedList<RecentRecordDto> _items = new(); // newest at the front

    public void Capture(string module, string? host, IReadOnlyList<Log>? logs, bool success, DateTime whenUtc) =>
        Enqueue(module, host, logs, l => l.Collection, success, whenUtc);

    public void Capture(string module, string? host, Log? log, bool success, DateTime whenUtc) =>
        Enqueue(module, host, log is null ? null : new[] { log }, l => l.Collection, success, whenUtc);

    public void Capture(string module, string? host, IReadOnlyList<LogBeat>? beats, bool success, DateTime whenUtc) =>
        Enqueue(module, host, beats, b => b.Collection, success, whenUtc);

    public void Capture(string module, string? host, LogBeat? beat, bool success, DateTime whenUtc) =>
        Enqueue(module, host, beat is null ? null : new[] { beat }, b => b.Collection, success, whenUtc);

    public void Capture(string module, string? host, IReadOnlyList<LogCache>? caches, bool success, DateTime whenUtc) =>
        Enqueue(module, host, caches, c => c.Collection, success, whenUtc);

    public void Capture(string module, string? host, LogCache? cache, bool success, DateTime whenUtc) =>
        Enqueue(module, host, cache is null ? null : new[] { cache }, c => c.Collection, success, whenUtc);

    /// <summary>Most recent records, newest first, capped at <paramref name="max"/>.</summary>
    public IReadOnlyList<RecentRecordDto> GetRecent(int max)
    {
        if (max <= 0 || max > Capacity) max = Capacity;
        lock (_lock)
        {
            return _items.Take(max).ToList();
        }
    }

    private void Enqueue<T>(
        string module,
        string? host,
        IReadOnlyList<T>? items,
        Func<T, string?> collectionOf,
        bool success,
        DateTime whenUtc) where T : class
    {
        if (items is not { Count: > 0 }) return;
        try
        {
            // Only the tail can survive in a Capacity-sized ring, so never serialize
            // more than that — a 70k-row batch costs at most Capacity serializations.
            var start = Math.Max(0, items.Count - Capacity);
            lock (_lock)
            {
                for (var i = start; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item is null) continue;
                    _items.AddFirst(new RecentRecordDto
                    {
                        WhenUtc = whenUtc,
                        Module = module,
                        Host = host ?? string.Empty,
                        Collection = collectionOf(item) ?? string.Empty,
                        Success = success,
                        Json = JsonSerializer.Serialize(item, item.GetType(), JsonOptions)
                    });
                    if (_items.Count > Capacity) _items.RemoveLast();
                }
            }
        }
        catch
        {
            // Telemetry must never affect delivery.
        }
    }
}
