using com.logdb.docker.collector.Models;

namespace com.logdb.docker.collector.Services;

/// <summary>
/// In-memory, per-minute time series of records the exporter handed to grpc-logger,
/// split by outcome (delivered / failed) plus batch counts, with container metrics
/// tracked on their own series. Feeds the Activity chart. Retains 24h at 1-minute
/// resolution; queries roll the raw minutes up to a coarser bucket for wider ranges.
/// </summary>
public class DeliveryActivityTracker
{
    private const int RetentionMinutes = 24 * 60;

    private readonly object _lock = new();
    private readonly SortedDictionary<long, Bucket> _minutes = new();

    private long _totalDelivered;
    private long _totalFailed;
    private long _totalBatches;
    private long _totalMetricsRecords;
    private long _totalMetricsBatches;

    private sealed class Bucket
    {
        public long Delivered;
        public long Failed;
        public long Batches;
        public long MetricsRecords;
        public long MetricsBatches;
    }

    /// <summary>
    /// Record one send (batch) with its record count and outcome
    /// ("delivered" vs anything else → failed). Set <paramref name="metrics"/>
    /// for container-metric sends so they're charted on their own series.
    /// </summary>
    public void Record(DateTime whenUtc, long records, string outcome, bool metrics)
    {
        if (records < 0) records = 0;
        var delivered = outcome == "delivered";
        var minute = ToMinute(whenUtc);

        lock (_lock)
        {
            if (!_minutes.TryGetValue(minute, out var b))
            {
                b = new Bucket();
                _minutes[minute] = b;
            }

            if (metrics)
            {
                b.MetricsBatches++;
                _totalMetricsBatches++;
                if (delivered) { b.MetricsRecords += records; _totalMetricsRecords += records; }
                else { b.Failed += records; _totalFailed += records; }
            }
            else
            {
                b.Batches++;
                _totalBatches++;
                if (delivered) { b.Delivered += records; _totalDelivered += records; }
                else { b.Failed += records; _totalFailed += records; }
            }

            Prune(minute);
        }
    }

    public ActivitySnapshot GetActivity(int rangeMinutes, DateTime nowUtc)
    {
        rangeMinutes = Math.Clamp(rangeMinutes, 1, RetentionMinutes);
        var bucketSeconds = BucketSecondsFor(rangeMinutes);
        var bucketMinutes = Math.Max(1, bucketSeconds / 60);
        var pointCount = Math.Max(1, rangeMinutes / bucketMinutes);

        var nowMinute = ToMinute(nowUtc);
        var buckets = new List<ActivityBucket>(pointCount);

        lock (_lock)
        {
            // Align the trailing edge to the bucket grid so windows are stable.
            var alignedEnd = nowMinute - (nowMinute % bucketMinutes);
            var startMinute = alignedEnd - (long)(pointCount - 1) * bucketMinutes;

            for (int i = 0; i < pointCount; i++)
            {
                var bStart = startMinute + (long)i * bucketMinutes;
                var agg = new ActivityBucket { StartUtc = FromMinute(bStart) };

                for (int m = 0; m < bucketMinutes; m++)
                {
                    if (_minutes.TryGetValue(bStart + m, out var raw))
                    {
                        agg.Delivered += raw.Delivered;
                        agg.Failed += raw.Failed;
                        agg.Batches += raw.Batches;
                        agg.MetricsRecords += raw.MetricsRecords;
                        agg.MetricsBatches += raw.MetricsBatches;
                    }
                }

                buckets.Add(agg);
            }

            return new ActivitySnapshot
            {
                Buckets = buckets,
                BucketSeconds = bucketSeconds,
                RangeMinutes = rangeMinutes,
                TotalDelivered = _totalDelivered,
                TotalFailed = _totalFailed,
                TotalBatches = _totalBatches,
                TotalMetricsRecords = _totalMetricsRecords,
                TotalMetricsBatches = _totalMetricsBatches,
                GeneratedUtc = nowUtc
            };
        }
    }

    // Caller holds _lock. Drops minutes older than the retention window.
    private void Prune(long currentMinute)
    {
        var cutoff = currentMinute - RetentionMinutes;
        while (_minutes.Count > 0)
        {
            long oldest = -1;
            foreach (var key in _minutes.Keys) { oldest = key; break; } // smallest (ascending)
            if (oldest >= 0 && oldest < cutoff) _minutes.Remove(oldest);
            else break;
        }
    }

    private static int BucketSecondsFor(int rangeMinutes) => rangeMinutes switch
    {
        <= 60 => 60,     // 15m, 1h  -> 1-minute buckets
        <= 360 => 300,   // 6h       -> 5-minute buckets
        _ => 900         // 24h      -> 15-minute buckets
    };

    private static long ToMinute(DateTime utc) => utc.Ticks / TimeSpan.TicksPerMinute;

    private static DateTime FromMinute(long minute) =>
        new DateTime(minute * TimeSpan.TicksPerMinute, DateTimeKind.Utc);
}
