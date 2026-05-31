namespace com.logdb.docker.collector.Services;

/// <summary>
/// Shared, thread-safe view of the spool replay worker's schedule so the UI can
/// show a live countdown to the next send. Updated by <see cref="SpoolReplayWorker"/>
/// at the end of each cycle (it knows the flush interval and when it will wake next).
/// </summary>
public class SpoolReplayState
{
    private long _nextCycleTicks;   // 0 = unknown
    private long _lastCycleTicks;   // 0 = never
    private int _intervalSeconds;
    private long _lastBatchCount;

    public void RecordCycle(int intervalSeconds, int lastBatchCount)
    {
        var now = DateTime.UtcNow;
        Interlocked.Exchange(ref _lastCycleTicks, now.Ticks);
        Interlocked.Exchange(ref _nextCycleTicks, now.AddSeconds(intervalSeconds).Ticks);
        Interlocked.Exchange(ref _intervalSeconds, intervalSeconds);
        Interlocked.Exchange(ref _lastBatchCount, lastBatchCount);
    }

    public SpoolReplaySnapshot GetSnapshot()
    {
        var next = ToUtc(Interlocked.Read(ref _nextCycleTicks));
        double? secondsUntilNext = next.HasValue
            ? Math.Max(0, (next.Value - DateTime.UtcNow).TotalSeconds)
            : null;

        return new SpoolReplaySnapshot
        {
            IntervalSeconds = Volatile.Read(ref _intervalSeconds),
            SecondsUntilNext = secondsUntilNext,
            NextCycleUtc = next,
            LastCycleUtc = ToUtc(Interlocked.Read(ref _lastCycleTicks)),
            LastBatchCount = (int)Interlocked.Read(ref _lastBatchCount)
        };
    }

    private static DateTime? ToUtc(long ticks) =>
        ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
}

public class SpoolReplaySnapshot
{
    public int IntervalSeconds { get; set; }
    /// <summary>Seconds until the next replay cycle, clamped at 0. Null until the worker has run once.</summary>
    public double? SecondsUntilNext { get; set; }
    public DateTime? NextCycleUtc { get; set; }
    public DateTime? LastCycleUtc { get; set; }
    public int LastBatchCount { get; set; }
}
