using System.Text.Json;
using com.logdb.docker.collector.Configuration;
using com.logdb.docker.collector.Models;
using Microsoft.Extensions.Options;

namespace com.logdb.docker.collector.Services;

/// <summary>
/// Tiny durable FIFO queue for container-metric records so a LogDB outage doesn't
/// lose them the way a direct send does. Metrics are low-volume (containers ×
/// collection interval), so a single NDJSON file with a record cap is plenty — no
/// segment rotation needed. The worker appends before sending and commits after a
/// successful send; failed sends stay queued and retry next cycle, and the queue
/// survives a process restart.
/// </summary>
public class MetricsSpoolStore
{
    private readonly ILogger<MetricsSpoolStore> _logger;
    private readonly SpoolOptions _options;
    private readonly object _lock = new();

    private string _filePath = "";
    private long _queued;
    private long _dropped;
    private long _replayed;

    public MetricsSpoolStore(ILogger<MetricsSpoolStore> logger, IOptions<SpoolOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public long QueuedRecords => Interlocked.Read(ref _queued);
    public long DroppedRecords => Interlocked.Read(ref _dropped);
    public long ReplayedRecords => Interlocked.Read(ref _replayed);

    public void Initialize()
    {
        if (!_options.Enabled) return;
        try
        {
            Directory.CreateDirectory(_options.DirectoryPath);
            _filePath = Path.Combine(_options.DirectoryPath, "metrics-spool.ndjson");
            Interlocked.Exchange(ref _queued, CountLines());
            _logger.LogInformation("Metrics spool initialized at {Path} with {Count} queued records", _filePath, _queued);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize metrics spool");
        }
    }

    public void Append(IReadOnlyList<DockerMetricsRecord> records)
    {
        if (!_options.Enabled || _filePath.Length == 0 || records.Count == 0) return;

        lock (_lock)
        {
            try
            {
                using (var writer = new StreamWriter(_filePath, append: true))
                {
                    foreach (var r in records)
                        writer.WriteLine(JsonSerializer.Serialize(r));
                }
                Interlocked.Add(ref _queued, records.Count);
                EnforceCapInternal();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Metrics spool append failed: {Msg}", ex.Message);
            }
        }
    }

    public List<DockerMetricsRecord> ReadBatch(int maxCount)
    {
        var result = new List<DockerMetricsRecord>();
        if (!_options.Enabled || _filePath.Length == 0) return result;

        lock (_lock)
        {
            try
            {
                if (!File.Exists(_filePath)) return result;
                foreach (var line in File.ReadLines(_filePath))
                {
                    if (result.Count >= maxCount) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var rec = JsonSerializer.Deserialize<DockerMetricsRecord>(line);
                    if (rec is not null) result.Add(rec);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Metrics spool read failed: {Msg}", ex.Message);
            }
        }
        return result;
    }

    public void CommitBatch(int count)
    {
        if (!_options.Enabled || _filePath.Length == 0 || count <= 0) return;

        lock (_lock)
        {
            try
            {
                if (!File.Exists(_filePath)) return;
                var lines = File.ReadAllLines(_filePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                var keep = lines.Count > count ? lines.Skip(count).ToList() : new List<string>();
                File.WriteAllLines(_filePath, keep);
                Interlocked.Add(ref _replayed, Math.Min(count, lines.Count));
                Interlocked.Exchange(ref _queued, keep.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Metrics spool commit failed: {Msg}", ex.Message);
            }
        }
    }

    // Caller holds _lock. Drops oldest records past MetricsMaxRecords.
    private void EnforceCapInternal()
    {
        var cap = Math.Max(1, _options.MetricsMaxRecords);
        try
        {
            if (!File.Exists(_filePath)) return;
            var lines = File.ReadAllLines(_filePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            if (lines.Count <= cap) return;

            var drop = lines.Count - cap;
            File.WriteAllLines(_filePath, lines.Skip(drop));
            Interlocked.Add(ref _dropped, drop);
            Interlocked.Exchange(ref _queued, cap);
            _logger.LogWarning("Metrics spool over cap ({Cap}): dropped {Drop} oldest records", cap, drop);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Metrics spool cap enforcement failed: {Msg}", ex.Message);
        }
    }

    private long CountLines()
    {
        try
        {
            return File.Exists(_filePath)
                ? File.ReadLines(_filePath).Count(l => !string.IsNullOrWhiteSpace(l))
                : 0;
        }
        catch { return 0; }
    }
}
