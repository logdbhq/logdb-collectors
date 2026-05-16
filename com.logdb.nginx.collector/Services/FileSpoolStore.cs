using System.Text.Json;
using com.logdb.nginx.collector.Configuration;
using com.logdb.nginx.collector.Models;
using Microsoft.Extensions.Options;

namespace com.logdb.nginx.collector.Services;

public class FileSpoolStore : ISpoolStore
{
    private readonly ILogger<FileSpoolStore> _logger;
    private SpoolOptions _options;
    private readonly Lock _lock = new();

    private string _activeSegmentPath = "";
    private long _activeSegmentBytes;
    private long _queuedRecords;
    private long _droppedRecords;
    private long _replayedRecords;
    private string? _lastError;

    public FileSpoolStore(ILogger<FileSpoolStore> logger, IOptions<SpoolOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public void Initialize()
    {
        if (!_options.Enabled) return;

        try
        {
            Directory.CreateDirectory(_options.DirectoryPath);
            RotateSegment();
            _queuedRecords = CountExistingRecords();
            _logger.LogInformation("Spool initialized at {Path} with {Count} queued records", _options.DirectoryPath, _queuedRecords);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogError(ex, "Failed to initialize spool at {Path}", _options.DirectoryPath);
        }
    }

    public void Append(NginxLogRecord record)
    {
        if (!_options.Enabled) return;

        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(record);
                var line = json + "\n";
                File.AppendAllText(_activeSegmentPath, line);
                _activeSegmentBytes += line.Length;
                Interlocked.Increment(ref _queuedRecords);

                if (_activeSegmentBytes >= _options.MaxSegmentBytes)
                    RotateSegment();

                EnforceLimitInternal();
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogError(ex, "Failed to append to spool");
            }
        }
    }

    public void AppendBatch(IReadOnlyList<NginxLogRecord> records)
    {
        if (!_options.Enabled || records.Count == 0) return;

        lock (_lock)
        {
            try
            {
                using var writer = new StreamWriter(_activeSegmentPath, append: true);
                foreach (var record in records)
                {
                    var json = JsonSerializer.Serialize(record);
                    writer.WriteLine(json);
                    _activeSegmentBytes += json.Length + 1;
                }
                Interlocked.Add(ref _queuedRecords, records.Count);

                if (_activeSegmentBytes >= _options.MaxSegmentBytes)
                    RotateSegment();

                EnforceLimitInternal();
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogError(ex, "Failed to append batch of {Count} records to spool", records.Count);
            }
        }
    }

    public List<NginxLogRecord> ReadBatch(int maxCount)
    {
        var records = new List<NginxLogRecord>();
        if (!_options.Enabled) return records;

        lock (_lock)
        {
            try
            {
                var segments = GetCompletedSegments();
                foreach (var seg in segments)
                {
                    if (records.Count >= maxCount) break;

                    var lines = File.ReadAllLines(seg);
                    foreach (var line in lines)
                    {
                        if (records.Count >= maxCount) break;
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        var record = JsonSerializer.Deserialize<NginxLogRecord>(line);
                        if (record is not null) records.Add(record);
                    }
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogError(ex, "Failed to read spool batch");
            }
        }

        return records;
    }

    public void CommitBatch(int count)
    {
        if (!_options.Enabled || count == 0) return;

        lock (_lock)
        {
            try
            {
                var remaining = count;
                var segments = GetCompletedSegments();

                foreach (var seg in segments)
                {
                    if (remaining <= 0) break;

                    var lines = File.ReadAllLines(seg).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                    if (lines.Count <= remaining)
                    {
                        remaining -= lines.Count;
                        File.Delete(seg);
                    }
                    else
                    {
                        var keepLines = lines.Skip(remaining).ToList();
                        File.WriteAllLines(seg, keepLines);
                        remaining = 0;
                    }
                }

                Interlocked.Add(ref _replayedRecords, count);
                Interlocked.Add(ref _queuedRecords, -count);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogError(ex, "Failed to commit spool batch");
            }
        }
    }

    public void EnforceLimit()
    {
        lock (_lock) { EnforceLimitInternal(); }
    }

    public void SetMaxDiskBytes(long maxBytes)
    {
        _options.MaxDiskBytes = maxBytes;
    }

    public SpoolStatus GetStatus()
    {
        return new SpoolStatus
        {
            Enabled = _options.Enabled,
            QueuedRecords = Interlocked.Read(ref _queuedRecords),
            DiskBytesUsed = GetDiskUsage(),
            MaxDiskBytes = _options.MaxDiskBytes,
            UtilizationPercent = _options.MaxDiskBytes > 0
                ? Math.Round(GetDiskUsage() * 100.0 / _options.MaxDiskBytes, 1)
                : 0,
            DroppedRecords = Interlocked.Read(ref _droppedRecords),
            ReplayedRecords = Interlocked.Read(ref _replayedRecords),
            LastError = _lastError
        };
    }

    public void Clear()
    {
        if (!_options.Enabled) return;

        lock (_lock)
        {
            try
            {
                var segments = Directory.GetFiles(_options.DirectoryPath, "seg-*.ndjson");
                long clearedRecords = 0;
                foreach (var seg in segments)
                {
                    try
                    {
                        if (seg != _activeSegmentPath)
                        {
                            var lineCount = File.ReadAllLines(seg).Count(l => !string.IsNullOrWhiteSpace(l));
                            File.Delete(seg);
                            clearedRecords += lineCount;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete spool segment {Segment}", seg);
                    }
                }

                // Clear the active segment by rotating to a new one
                if (File.Exists(_activeSegmentPath))
                {
                    var activeLines = File.ReadAllLines(_activeSegmentPath).Count(l => !string.IsNullOrWhiteSpace(l));
                    File.Delete(_activeSegmentPath);
                    clearedRecords += activeLines;
                }
                RotateSegment();

                Interlocked.Add(ref _queuedRecords, -clearedRecords);
                if (_queuedRecords < 0) Interlocked.Exchange(ref _queuedRecords, 0);

                _logger.LogInformation("Spool cleared: removed {Count} records", clearedRecords);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogError(ex, "Failed to clear spool");
            }
        }
    }

    private void RotateSegment()
    {
        _activeSegmentPath = Path.Combine(_options.DirectoryPath, $"seg-{DateTime.UtcNow:yyyyMMddHHmmssfff}.ndjson");
        _activeSegmentBytes = 0;
    }

    private string[] GetCompletedSegments()
    {
        return Directory.GetFiles(_options.DirectoryPath, "seg-*.ndjson")
            .Where(f => f != _activeSegmentPath)
            .OrderBy(f => f)
            .ToArray();
    }

    private void EnforceLimitInternal()
    {
        if (_options.WhenFull == OverflowPolicy.RejectWrites) return;

        var usage = GetDiskUsage();
        if (usage <= _options.MaxDiskBytes) return;

        var segments = GetCompletedSegments();
        foreach (var seg in segments)
        {
            if (GetDiskUsage() <= _options.MaxDiskBytes) break;

            try
            {
                var lineCount = File.ReadAllLines(seg).Count(l => !string.IsNullOrWhiteSpace(l));
                File.Delete(seg);
                Interlocked.Add(ref _droppedRecords, lineCount);
                Interlocked.Add(ref _queuedRecords, -lineCount);
                _logger.LogWarning("Dropped spool segment {Segment} ({Lines} records) due to disk limit", seg, lineCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete spool segment {Segment}", seg);
            }
        }
    }

    private long GetDiskUsage()
    {
        try
        {
            return Directory.GetFiles(_options.DirectoryPath, "seg-*.ndjson")
                .Sum(f => new FileInfo(f).Length);
        }
        catch { return 0; }
    }

    private long CountExistingRecords()
    {
        try
        {
            return Directory.GetFiles(_options.DirectoryPath, "seg-*.ndjson")
                .Sum(f => File.ReadAllLines(f).Count(l => !string.IsNullOrWhiteSpace(l)));
        }
        catch { return 0; }
    }
}
