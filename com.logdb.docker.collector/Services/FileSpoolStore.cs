using System.Text.Json;
using com.logdb.docker.collector.Configuration;
using com.logdb.docker.collector.Models;
using Microsoft.Extensions.Options;

namespace com.logdb.docker.collector.Services;

public class FileSpoolStore : ISpoolStore, IDisposable
{
    private readonly ILogger<FileSpoolStore> _logger;
    private readonly SpoolOptions _options;
    private readonly string _directory;
    private readonly object _lock = new();

    private readonly SortedList<string, SegmentInfo> _segments = new();
    private StreamWriter? _activeWriter;
    private string? _activeSegmentPath;
    private long _activeSegmentBytes;

    private long _maxDiskBytesOverride;

    private long _totalDroppedSinceLastLog;
    private DateTime _lastDropLogUtc;
    private long _queuedRecords;
    private long _diskBytesUsed;
    private long _replayedRecords;
    private long _droppedRecords;
    private DateTime? _oldestRecordUtc;
    private DateTime? _lastWriteUtc;
    private DateTime? _lastReplayUtc;
    private string? _lastError;

    // Tracks how many records have been read but not yet committed from the oldest segment
    private int _pendingReadCount;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FileSpoolStore(ILogger<FileSpoolStore> logger, IOptions<SpoolOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        _directory = Path.GetFullPath(_options.DirectoryPath);
    }

    private long MaxDiskBytes => _maxDiskBytesOverride > 0 ? _maxDiskBytesOverride : _options.MaxDiskBytes;

    public void SetMaxDiskBytes(long maxBytes)
    {
        lock (_lock)
        {
            _maxDiskBytesOverride = maxBytes;
            _logger.LogInformation("Spool max disk bytes updated to {MaxBytes} MB", maxBytes / 1_048_576);
        }
    }

    public void Initialize()
    {
        if (!_options.Enabled) return;

        try
        {
            Directory.CreateDirectory(_directory);
            ScanExistingSegments();
            _logger.LogInformation("Spool ready: {Count} segments, {Records} queued", _segments.Count, _queuedRecords);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogWarning("Spool init failed: {Msg}", ex.Message);
        }
    }

    public void Append(LogRecord record)
    {
        if (!_options.Enabled) return;

        lock (_lock)
        {
            try
            {
                // Check disk limit
                if (_diskBytesUsed >= MaxDiskBytes)
                {
                    switch (_options.WhenFull)
                    {
                        case OverflowPolicy.DropNewest:
                            _droppedRecords++;
                            return;
                        case OverflowPolicy.RejectWrites:
                            _droppedRecords++;
                            return;
                        case OverflowPolicy.DropOldest:
                            DropOldestSegment();
                            break;
                    }
                }

                EnsureActiveWriter();

                var line = JsonSerializer.Serialize(record, JsonOpts);
                _activeWriter!.WriteLine(line);
                _activeWriter.Flush();

                var lineBytes = System.Text.Encoding.UTF8.GetByteCount(line) + 1; // +1 for newline
                _activeSegmentBytes += lineBytes;
                _diskBytesUsed += lineBytes;
                _queuedRecords++;
                _lastWriteUtc = DateTime.UtcNow;

                // Update segment record count
                if (_activeSegmentPath is not null && _segments.TryGetValue(_activeSegmentPath, out var seg))
                    seg.RecordCount++;

                // Rotate if segment is too large
                if (_activeSegmentBytes >= _options.MaxSegmentBytes)
                    RotateActiveSegment();
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogWarning("Spool append failed: {Msg}", ex.Message);
            }
        }
    }

    public void AppendBatch(IReadOnlyList<LogRecord> records)
    {
        if (!_options.Enabled || records.Count == 0) return;

        lock (_lock)
        {
            try
            {
                foreach (var record in records)
                {
                    if (_diskBytesUsed >= MaxDiskBytes)
                    {
                        switch (_options.WhenFull)
                        {
                            case OverflowPolicy.DropNewest:
                            case OverflowPolicy.RejectWrites:
                                _droppedRecords++;
                                continue;
                            case OverflowPolicy.DropOldest:
                                DropOldestSegment();
                                break;
                        }
                    }

                    EnsureActiveWriter();

                    var line = JsonSerializer.Serialize(record, JsonOpts);
                    _activeWriter!.WriteLine(line);

                    var lineBytes = System.Text.Encoding.UTF8.GetByteCount(line) + 1;
                    _activeSegmentBytes += lineBytes;
                    _diskBytesUsed += lineBytes;
                    _queuedRecords++;

                    if (_activeSegmentPath is not null && _segments.TryGetValue(_activeSegmentPath, out var seg))
                        seg.RecordCount++;

                    if (_activeSegmentBytes >= _options.MaxSegmentBytes)
                    {
                        _activeWriter.Flush();
                        RotateActiveSegment();
                    }
                }

                _activeWriter?.Flush();
                _lastWriteUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogWarning("Spool append batch failed: {Msg}", ex.Message);
            }
        }
    }

    public List<LogRecord> ReadBatch(int maxCount)
    {
        if (!_options.Enabled) return new();

        lock (_lock)
        {
            var records = new List<LogRecord>();

            try
            {
                // Seal the active segment so records ingested since the last cycle
                // become part of THIS batch instead of waiting for the segment to
                // reach MaxSegmentBytes — otherwise recent low-volume logs show
                // live but are never sent.
                if (_activeSegmentBytes > 0)
                    RotateActiveSegment();

                var segmentPaths = _segments.Keys.ToList();

                foreach (var segPath in segmentPaths)
                {
                    if (records.Count >= maxCount) break;
                    if (segPath == _activeSegmentPath) continue; // Skip active segment

                    var remaining = maxCount - records.Count;
                    var segRecords = ReadSegmentRecords(segPath, remaining);
                    records.AddRange(segRecords);
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogWarning("Spool read failed: {Msg}", ex.Message);
            }

            _pendingReadCount = records.Count;
            return records;
        }
    }

    public void CommitBatch(int count)
    {
        if (!_options.Enabled || count == 0) return;

        lock (_lock)
        {
            try
            {
                var remaining = count;
                var segmentPaths = _segments.Keys.ToList();

                foreach (var segPath in segmentPaths)
                {
                    if (remaining <= 0) break;
                    if (segPath == _activeSegmentPath) continue;

                    if (!_segments.TryGetValue(segPath, out var seg)) continue;

                    if (seg.RecordCount <= remaining)
                    {
                        remaining -= seg.RecordCount;
                        _queuedRecords -= seg.RecordCount;
                        _replayedRecords += seg.RecordCount;
                        _diskBytesUsed -= seg.ByteSize;
                        _segments.Remove(segPath);

                        try { File.Delete(segPath); }
                        catch { /* best effort */ }
                    }
                    else
                    {
                        // Partial commit - rewrite segment without consumed records
                        var allRecords = ReadAllSegmentRecords(segPath);
                        var kept = allRecords.Skip(remaining).ToList();

                        _queuedRecords -= remaining;
                        _replayedRecords += remaining;

                        RewriteSegment(segPath, kept);
                        remaining = 0;
                    }
                }

                _lastReplayUtc = DateTime.UtcNow;
                _pendingReadCount = 0;

                UpdateOldestTimestamp();
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogWarning("Spool commit failed: {Msg}", ex.Message);
            }
        }
    }

    public void EnforceLimit()
    {
        if (!_options.Enabled) return;

        lock (_lock)
        {
            while (_diskBytesUsed > MaxDiskBytes && _segments.Count > 1)
            {
                DropOldestSegment();
            }
        }
    }

    public SpoolStatus GetStatus()
    {
        lock (_lock)
        {
            var maxBytes = MaxDiskBytes;
            var utilization = maxBytes > 0 ? (int)(_diskBytesUsed * 100 / maxBytes) : 0;

            return new SpoolStatus
            {
                Enabled = _options.Enabled,
                DirectoryPath = _directory,
                QueuedRecords = _queuedRecords,
                DiskBytesUsed = _diskBytesUsed,
                MaxDiskBytes = maxBytes,
                UtilizationPercent = utilization,
                ReplayedRecords = _replayedRecords,
                DroppedRecords = _droppedRecords,
                OldestRecordUtc = _oldestRecordUtc,
                LastWriteUtc = _lastWriteUtc,
                LastReplayUtc = _lastReplayUtc,
                LastError = _lastError
            };
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _activeWriter?.Dispose();
            _activeWriter = null;
            _activeSegmentPath = null;
            _activeSegmentBytes = 0;

            foreach (var segPath in _segments.Keys.ToList())
            {
                try { File.Delete(segPath); }
                catch { /* best effort */ }
            }

            _segments.Clear();
            _queuedRecords = 0;
            _diskBytesUsed = 0;
            _oldestRecordUtc = null;
            _pendingReadCount = 0;
            _logger.LogInformation("Spool cleared - all segments deleted");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _activeWriter?.Dispose();
            _activeWriter = null;
        }
    }

    // --- Private helpers ---

    private void ScanExistingSegments()
    {
        var files = Directory.GetFiles(_directory, "seg-*.ndjson").OrderBy(f => f).ToList();

        foreach (var file in files)
        {
            var info = new FileInfo(file);
            var recordCount = CountLines(file);

            _segments[file] = new SegmentInfo { ByteSize = info.Length, RecordCount = recordCount };
            _diskBytesUsed += info.Length;
            _queuedRecords += recordCount;
        }

        UpdateOldestTimestamp();
    }

    private void EnsureActiveWriter()
    {
        if (_activeWriter is not null) return;

        var name = $"seg-{DateTime.UtcNow:yyyyMMddHHmmssfff}.ndjson";
        _activeSegmentPath = Path.Combine(_directory, name);
        _activeWriter = new StreamWriter(_activeSegmentPath, append: true);
        _activeSegmentBytes = 0;

        _segments[_activeSegmentPath] = new SegmentInfo { ByteSize = 0, RecordCount = 0 };
    }

    private void RotateActiveSegment()
    {
        if (_activeWriter is null) return;

        // Update final byte size
        if (_activeSegmentPath is not null && _segments.TryGetValue(_activeSegmentPath, out var seg))
            seg.ByteSize = _activeSegmentBytes;

        _activeWriter.Dispose();
        _activeWriter = null;
        _activeSegmentPath = null;
        _activeSegmentBytes = 0;
    }

    private void DropOldestSegment()
    {
        if (_segments.Count == 0) return;

        var oldest = _segments.Keys[0];
        if (oldest == _activeSegmentPath && _segments.Count == 1) return;
        if (oldest == _activeSegmentPath) oldest = _segments.Keys[1];

        if (_segments.TryGetValue(oldest, out var seg))
        {
            _droppedRecords += seg.RecordCount;
            _queuedRecords -= seg.RecordCount;
            _diskBytesUsed -= seg.ByteSize;
            _segments.Remove(oldest);

            try { File.Delete(oldest); }
            catch { /* best effort */ }

            _totalDroppedSinceLastLog += seg.RecordCount;
            if ((DateTime.UtcNow - _lastDropLogUtc).TotalSeconds >= 30)
            {
                _logger.LogWarning("Spool overflow: dropped {Count} records since last report", _totalDroppedSinceLastLog);
                _totalDroppedSinceLastLog = 0;
                _lastDropLogUtc = DateTime.UtcNow;
            }
        }
    }

    private List<LogRecord> ReadSegmentRecords(string path, int maxCount)
    {
        var records = new List<LogRecord>();
        using var reader = new StreamReader(path);

        string? line;
        while (records.Count < maxCount && (line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var record = JsonSerializer.Deserialize<LogRecord>(line, JsonOpts);
                if (record is not null) records.Add(record);
            }
            catch (JsonException)
            {
                _logger.LogDebug("Skipping corrupt spool line in {Path}", path);
            }
        }

        return records;
    }

    private List<LogRecord> ReadAllSegmentRecords(string path)
    {
        return ReadSegmentRecords(path, int.MaxValue);
    }

    private void RewriteSegment(string path, List<LogRecord> records)
    {
        var tempPath = path + ".tmp";
        long bytes = 0;

        using (var writer = new StreamWriter(tempPath))
        {
            foreach (var record in records)
            {
                var line = JsonSerializer.Serialize(record, JsonOpts);
                writer.WriteLine(line);
                bytes += System.Text.Encoding.UTF8.GetByteCount(line) + 1;
            }
        }

        File.Move(tempPath, path, overwrite: true);

        if (_segments.TryGetValue(path, out var seg))
        {
            var oldBytes = seg.ByteSize;
            seg.ByteSize = bytes;
            seg.RecordCount = records.Count;
            _diskBytesUsed += bytes - oldBytes;
        }
    }

    private void UpdateOldestTimestamp()
    {
        _oldestRecordUtc = null;

        if (_segments.Count == 0) return;

        var oldestPath = _segments.Keys[0];
        try
        {
            using var reader = new StreamReader(oldestPath);
            var line = reader.ReadLine();
            if (line is not null)
            {
                var record = JsonSerializer.Deserialize<LogRecord>(line, JsonOpts);
                if (record is not null) _oldestRecordUtc = record.Timestamp;
            }
        }
        catch { /* best effort */ }
    }

    private static int CountLines(string path)
    {
        var count = 0;
        using var reader = new StreamReader(path);
        while (reader.ReadLine() is not null) count++;
        return count;
    }

    private class SegmentInfo
    {
        public long ByteSize { get; set; }
        public int RecordCount { get; set; }
    }
}
