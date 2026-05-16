using System.Text.Json;
using System.Text.RegularExpressions;
using com.logdb.docker.collector.Models;

namespace com.logdb.docker.collector.Services;

public class DockerFileTailService : IFileTailService
{
    private readonly IDockerDiscoveryService _discovery;
    private readonly ICheckpointStore _checkpointStore;
    private readonly ContainerToggleService _toggleService;
    private readonly ILogRecordSink _sink;
    private readonly ILogger<DockerFileTailService> _logger;
    private readonly string _hostName;
    private readonly object _lock = new();

    private List<TailTarget> _targets = new();
    private readonly Dictionary<string, long> _offsets = new();
    private readonly Dictionary<string, DateTime> _lastRead = new();

    private long _recordsRead;
    private long _parseErrors;
    private long _readErrors;
    private long _parseErrorsSinceLastLog;
    private DateTime _lastParseErrorLogUtc;
    private DateTime? _lastRecordTimestamp;

    public DockerFileTailService(
        IDockerDiscoveryService discovery,
        ICheckpointStore checkpointStore,
        ContainerToggleService toggleService,
        ILogRecordSink sink,
        ILogger<DockerFileTailService> logger)
    {
        _discovery = discovery;
        _checkpointStore = checkpointStore;
        _toggleService = toggleService;
        _sink = sink;
        _logger = logger;
        _hostName = Environment.MachineName;
    }

    public void ResetOffsets()
    {
        lock (_lock)
        {
            // Set to 0 explicitly - don't remove, otherwise TailFileAsync
            // treats it as "never seen" and defaults to end-of-file
            foreach (var key in _offsets.Keys.ToList())
                _offsets[key] = 0;
            _lastRead.Clear();
        }
    }

    public void ResetOffset(string containerId)
    {
        lock (_lock)
        {
            var keys = _targets
                .Where(t => t.ContainerId.Equals(containerId, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.LogPath)
                .ToList();

            foreach (var key in keys)
            {
                _offsets[key] = 0;
                _lastRead.Remove(key);
            }
        }
    }

    public IReadOnlyList<TailTarget> GetTargets()
    {
        lock (_lock) { return _targets.ToList(); }
    }

    public PipelineStatus GetPipelineStatus()
    {
        lock (_lock)
        {
            var offsets = new Dictionary<string, FileOffsetInfo>();
            foreach (var (path, offset) in _offsets)
            {
                var target = _targets.FirstOrDefault(t => t.LogPath == path);
                offsets[path] = new FileOffsetInfo
                {
                    ContainerName = target?.ContainerName ?? "unknown",
                    Offset = offset,
                    LastReadUtc = _lastRead.TryGetValue(path, out var lr) ? lr : null
                };
            }

            return new PipelineStatus
            {
                ActiveTargets = _targets.Count,
                RecordsRead = _recordsRead,
                ParseErrors = _parseErrors,
                ReadErrors = _readErrors,
                LastRecordTimestamp = _lastRecordTimestamp,
                FileOffsets = offsets
            };
        }
    }

    private const int TailBatchSize = 4096;

    public async Task<bool> TailAsync(CancellationToken cancellationToken = default)
    {
        var containers = _discovery.GetContainers();
        var included = containers.Where(c => c.IsIncluded && !string.IsNullOrEmpty(c.LogPath)).ToList();

        var targets = included.Select(c => new TailTarget
        {
            ContainerId = c.Id,
            ContainerName = c.Name,
            Image = c.Image,
            ImageTag = c.ImageTag,
            LogPath = c.LogPath,
            ComposeProject = c.ComposeProject,
            ComposeService = c.ComposeService,
            Labels = new Dictionary<string, string>(c.Labels)
        }).ToList();

        lock (_lock) { _targets = targets; }

        var recordsBefore = Interlocked.Read(ref _recordsRead);

        var batch = new List<LogRecord>(TailBatchSize);
        foreach (var target in targets)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await TailFileAsync(target, batch, cancellationToken);
        }

        // Flush remaining
        if (batch.Count > 0)
        {
            _sink.WriteBatch(batch);
            batch.Clear();
        }

        return Interlocked.Read(ref _recordsRead) > recordsBefore;
    }

    private async Task TailFileAsync(TailTarget target, List<LogRecord> batch, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(target.LogPath))
            {
                _logger.LogDebug("Log not found: {Container}", target.ContainerName);
                return;
            }

            var fileInfo = new FileInfo(target.LogPath);
            long currentOffset;
            lock (_lock)
            {
                if (!_offsets.TryGetValue(target.LogPath, out currentOffset))
                {
                    // First time seeing this file - try to restore from persisted checkpoint
                    currentOffset = _checkpointStore.GetOffset(target.LogPath);

                    // No checkpoint - use start date or default to end of file
                    if (currentOffset == 0)
                    {
                        var startDate = _toggleService.GetContainerStartDate(target.ContainerId);
                        if (startDate is not null)
                        {
                            currentOffset = FindOffsetForDate(target.LogPath, startDate.Value);
                        }
                        else
                        {
                            // No start date configured - start from current end of file
                            currentOffset = fileInfo.Length;
                        }
                    }

                    _offsets[target.LogPath] = currentOffset;
                }

                // Handle rotation: if file is smaller than our offset, reset
                if (fileInfo.Length < currentOffset)
                {
                    _logger.LogDebug("File rotated: {Container} (size {Size} < offset {Offset})",
                        target.ContainerName, fileInfo.Length, currentOffset);
                    currentOffset = 0;
                }
            }

            if (fileInfo.Length == currentOffset) return;

            using var stream = new FileStream(target.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(currentOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);

            LogRecord? buffered = null;
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var record = ParseDockerJsonLog(line, target);
                if (record is null) continue;

                if (buffered is not null && IsContinuationLine(record.Message))
                {
                    // Append to previous record as multiline
                    buffered.Message += "\n" + record.Message;
                }
                else
                {
                    // Flush previous buffered record into batch
                    if (buffered is not null)
                    {
                        NormalizeDotNetLoggerOutput(buffered);
                        Interlocked.Increment(ref _recordsRead);
                        lock (_lock) { _lastRecordTimestamp = buffered.Timestamp; }
                        batch.Add(buffered);

                        if (batch.Count >= TailBatchSize)
                        {
                            _sink.WriteBatch(batch);
                            batch.Clear();
                        }
                    }
                    buffered = record;
                }
            }

            // Flush last buffered record into batch
            if (buffered is not null)
            {
                NormalizeDotNetLoggerOutput(buffered);
                Interlocked.Increment(ref _recordsRead);
                lock (_lock) { _lastRecordTimestamp = buffered.Timestamp; }
                batch.Add(buffered);

                if (batch.Count >= TailBatchSize)
                {
                    _sink.WriteBatch(batch);
                    batch.Clear();
                }
            }

            var newOffset = stream.Position;
            lock (_lock)
            {
                _offsets[target.LogPath] = newOffset;
                _lastRead[target.LogPath] = DateTime.UtcNow;
            }

            _checkpointStore.UpdateCheckpoint(target.LogPath, target.ContainerId, target.ContainerName, newOffset);
        }
        catch (IOException ex)
        {
            Interlocked.Increment(ref _readErrors);
            _logger.LogWarning("Tail read error [{Container}]: {Msg}", target.ContainerName, ex.Message);
        }
    }

    private LogRecord? ParseDockerJsonLog(string line, TailTarget target)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var log = root.TryGetProperty("log", out var logProp) ? logProp.GetString() ?? "" : "";
            var stream = root.TryGetProperty("stream", out var streamProp) ? streamProp.GetString() ?? "" : "";
            var time = root.TryGetProperty("time", out var timeProp) ? timeProp.GetString() : null;

            DateTime timestamp;
            if (time is not null && DateTime.TryParse(time, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                timestamp = parsed;
            else
                timestamp = DateTime.UtcNow;

            return new LogRecord
            {
                Timestamp = timestamp,
                Message = log.TrimEnd('\n'),
                Stream = stream.TrimEnd('\n'),
                ContainerId = target.ContainerId,
                ContainerName = target.ContainerName,
                Image = target.Image,
                HostName = _hostName,
                Labels = target.Labels,
                ComposeProject = target.ComposeProject,
                ComposeService = target.ComposeService
            };
        }
        catch (JsonException)
        {
            Interlocked.Increment(ref _parseErrors);
            Interlocked.Increment(ref _parseErrorsSinceLastLog);
            if ((DateTime.UtcNow - _lastParseErrorLogUtc).TotalSeconds >= 60)
            {
                _logger.LogDebug("Parse errors: {Count} since last report (latest: {Container})",
                    Interlocked.Exchange(ref _parseErrorsSinceLastLog, 0), target.ContainerName);
                _lastParseErrorLogUtc = DateTime.UtcNow;
            }
            return null;
        }
    }

    // .NET ConsoleLogger header: "info: Some.Namespace.Class[0]" or "fail: Some.Class[42]"
    private static readonly Regex DotNetLoggerHeaderRegex = new(
        @"^(info|warn|fail|dbug|crit|trce):\s+(.+?)\[(\d+)\]$", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> DotNetLevelMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["trce"] = "Trace", ["dbug"] = "Debug", ["info"] = "Info",
        ["warn"] = "Warning", ["fail"] = "Error", ["crit"] = "Critical"
    };

    /// <summary>
    /// After multiline merge, if the first line is a .NET ConsoleLogger header
    /// (e.g. "info: SomeNamespace.SomeClass[0]"), extract the category and level,
    /// and promote the actual content lines to be the message.
    /// </summary>
    private static void NormalizeDotNetLoggerOutput(LogRecord record)
    {
        var msg = record.Message;
        var newlineIdx = msg.IndexOf('\n');

        // Need at least a header + content line to normalize
        string firstLine;
        string rest;
        if (newlineIdx > 0)
        {
            firstLine = msg[..newlineIdx];
            rest = msg[(newlineIdx + 1)..];
        }
        else
        {
            firstLine = msg;
            rest = "";
        }

        var match = DotNetLoggerHeaderRegex.Match(firstLine);
        if (!match.Success) return;

        record.Category = match.Groups[2].Value;
        record.ParsedLevel = DotNetLevelMap.GetValueOrDefault(match.Groups[1].Value, "Info");

        if (!string.IsNullOrWhiteSpace(rest))
        {
            // Trim leading whitespace from each content line (ConsoleLogger indents with spaces)
            var lines = rest.Split('\n');
            var trimmed = string.Join("\n", lines.Select(l => l.TrimStart()));
            record.Message = trimmed;
        }
        // If there's no content after the header, keep the header as the message
    }

    // Matches fully qualified .NET exception types, e.g. "System.InvalidOperationException: ..."
    // or "Newtonsoft.Json.JsonReaderException: ..."
    private static readonly Regex ExceptionTypeRegex = new(
        @"^[\w+]+(\.[\w+]+)+Exception\b", RegexOptions.Compiled);

    private static bool IsContinuationLine(string message)
    {
        if (string.IsNullOrEmpty(message)) return false;

        // Stack trace frames: "   at Namespace.Class.Method(...)"
        // Also covers "   --- End of inner exception stack trace ---"
        if (message[0] is ' ' or '\t') return true;

        // "at Namespace.Class.Method(...)" without leading whitespace (some formatters)
        if (message.StartsWith("at ", StringComparison.Ordinal)) return true;

        // Inner exception separator: "--- End of ..." or "---> System.Exception"
        if (message.StartsWith("---", StringComparison.Ordinal)) return true;

        // Bare exception type line: "Newtonsoft.Json.JsonReaderException: Input string..."
        if (ExceptionTypeRegex.IsMatch(message)) return true;

        return false;
    }

    public LogSizeEstimate EstimateSize(string logPath, DateTime fromUtc)
    {
        var estimate = new LogSizeEstimate { LogPath = logPath };

        try
        {
            if (!File.Exists(logPath))
                return estimate;

            var fileInfo = new FileInfo(logPath);
            estimate.TotalFileBytes = fileInfo.Length;

            var offset = FindOffsetForDate(logPath, fromUtc);
            estimate.EstimatedExportBytes = fileInfo.Length - offset;

            // Count lines and get timestamps from the export range
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);

            long lineCount = 0;
            DateTime? earliest = null;
            DateTime? latest = null;

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                lineCount++;

                // Only parse timestamps for first and last few lines to keep it fast
                if (lineCount <= 1 || reader.EndOfStream)
                {
                    var ts = ExtractTimestamp(line);
                    if (ts is not null)
                    {
                        earliest ??= ts;
                        latest = ts;
                    }
                }
            }

            estimate.EstimatedLineCount = lineCount;
            estimate.EarliestTimestamp = earliest;
            estimate.LatestTimestamp = latest;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to estimate size for {Path}", logPath);
        }

        return estimate;
    }

    /// <summary>
    /// Binary search through the log file to find the byte offset where entries >= fromUtc begin.
    /// Docker JSON log files are chronologically ordered.
    /// </summary>
    private long FindOffsetForDate(string logPath, DateTime fromUtc)
    {
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var fileLength = stream.Length;
            if (fileLength == 0) return 0;

            long lo = 0, hi = fileLength;

            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                stream.Seek(mid, SeekOrigin.Begin);

                // Skip to start of next complete line
                using var reader = new StreamReader(stream, leaveOpen: true);
                if (mid > 0) reader.ReadLine(); // discard partial line

                var line = reader.ReadLine();
                if (line is null)
                {
                    hi = mid;
                    continue;
                }

                var ts = ExtractTimestamp(line);
                if (ts is null)
                {
                    // Can't parse - move forward
                    lo = mid + 1;
                    continue;
                }

                if (ts.Value < fromUtc)
                    lo = stream.Position;
                else
                    hi = mid;
            }

            // Align to line boundary
            stream.Seek(lo, SeekOrigin.Begin);
            if (lo > 0)
            {
                using var reader = new StreamReader(stream, leaveOpen: true);
                reader.ReadLine(); // skip partial
                return stream.Position;
            }

            return lo;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to find offset for date in {Path}", logPath);
            return 0;
        }
    }

    private static DateTime? ExtractTimestamp(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var time = doc.RootElement.TryGetProperty("time", out var timeProp) ? timeProp.GetString() : null;
            if (time is not null && DateTime.TryParse(time, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                return parsed;
        }
        catch { /* best effort */ }
        return null;
    }
}
