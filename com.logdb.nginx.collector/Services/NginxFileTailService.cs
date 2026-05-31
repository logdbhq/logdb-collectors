using System.Text;
using com.logdb.nginx.collector.Configuration;
using com.logdb.nginx.collector.Models;
using com.logdb.nginx.collector.Parsing;
using Microsoft.Extensions.Options;

namespace com.logdb.nginx.collector.Services;

public class NginxFileTailService : IFileTailService
{
    private readonly ILogger<NginxFileTailService> _logger;
    private readonly NginxTargetOptions _targetOptions;
    private readonly ICheckpointStore _checkpointStore;
    private readonly ILogRecordSink _sink;
    private readonly TargetToggleService _toggleService;
    private readonly FilterRuleService _filterService;
    private readonly TargetDiscoveryService _discovery;
    private readonly string _hostName;

    private long _accessRecordsRead;
    private long _errorRecordsRead;
    private long _parseErrors;
    private long _readErrors;
    private long _filteredStaticFiles;
    private long _filteredByRules;
    private long _rotationsDetected;
    private DateTime? _lastRecordTimestamp;
    private DateTime? _lastTailCycleUtc;

    public NginxFileTailService(
        ILogger<NginxFileTailService> logger,
        IOptions<NginxTargetOptions> targetOptions,
        ICheckpointStore checkpointStore,
        ILogRecordSink sink,
        TargetToggleService toggleService,
        FilterRuleService filterService,
        TargetDiscoveryService discovery)
    {
        _logger = logger;
        _targetOptions = targetOptions.Value;
        _checkpointStore = checkpointStore;
        _sink = sink;
        _toggleService = toggleService;
        _filterService = filterService;
        _discovery = discovery;
        _hostName = Environment.MachineName;
    }

    /// <summary>
    /// Explicit (config) targets merged with auto-discovered ones. Discovered
    /// files already covered by an explicit target are skipped to avoid
    /// double-tailing the same file.
    /// </summary>
    private List<NginxTarget> GetConfiguredTargets()
    {
        var configured = _targetOptions.Targets;
        if (!_discovery.Enabled)
            return configured.ToList();

        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in configured)
        {
            if (!string.IsNullOrEmpty(t.AccessLogPath)) tracked.Add(SafeFullPath(t.AccessLogPath));
            if (!string.IsNullOrEmpty(t.ErrorLogPath)) tracked.Add(SafeFullPath(t.ErrorLogPath));
        }

        return configured.Concat(_discovery.DiscoverTargets(tracked)).ToList();
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    public IReadOnlyList<TailTarget> GetTargets()
    {
        var targets = new List<TailTarget>();
        foreach (var t in GetConfiguredTargets().Where(t => t.Enabled))
        {
            if (!string.IsNullOrEmpty(t.AccessLogPath) && _toggleService.IsAccessLogEnabled(t.Name))
                targets.Add(new TailTarget { TargetName = t.Name, FilePath = t.AccessLogPath, LogType = NginxLogType.Access, Enabled = true });
            if (!string.IsNullOrEmpty(t.ErrorLogPath) && _toggleService.IsErrorLogEnabled(t.Name))
                targets.Add(new TailTarget { TargetName = t.Name, FilePath = t.ErrorLogPath, LogType = NginxLogType.Error, Enabled = true });
        }
        return targets;
    }

    public PipelineStatus GetPipelineStatus()
    {
        var targets = GetTargets();
        return new PipelineStatus
        {
            ActiveTargets = GetConfiguredTargets().Count(t => t.Enabled),
            ActiveFiles = targets.Count(t => File.Exists(t.FilePath)),
            AccessRecordsRead = Interlocked.Read(ref _accessRecordsRead),
            ErrorRecordsRead = Interlocked.Read(ref _errorRecordsRead),
            ParseErrors = Interlocked.Read(ref _parseErrors),
            ReadErrors = Interlocked.Read(ref _readErrors),
            FilteredStaticFiles = Interlocked.Read(ref _filteredStaticFiles),
            FilteredByRules = Interlocked.Read(ref _filteredByRules),
            RotationsDetected = Interlocked.Read(ref _rotationsDetected),
            LastRecordTimestamp = _lastRecordTimestamp,
            LastTailCycleUtc = _lastTailCycleUtc
        };
    }

    public async Task<bool> TailAsync(CancellationToken cancellationToken = default)
    {
        var beforeAccess = Interlocked.Read(ref _accessRecordsRead);
        var beforeError = Interlocked.Read(ref _errorRecordsRead);

        var targets = GetTargets();
        foreach (var target in targets)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await TailFileAsync(target, cancellationToken);
        }
        _lastTailCycleUtc = DateTime.UtcNow;

        return Interlocked.Read(ref _accessRecordsRead) > beforeAccess
            || Interlocked.Read(ref _errorRecordsRead) > beforeError;
    }

    private Task TailFileAsync(TailTarget target, CancellationToken ct)
    {
        if (!File.Exists(target.FilePath))
            return Task.CompletedTask;

        try
        {
            var fileInfo = new FileInfo(target.FilePath);
            var offset = ResolveOffset(target, fileInfo);
            var fileCreatedUtc = GetFileCreationTimeUtc(target.FilePath);

            // Nothing new to read. Persist the (possibly reset) offset and current
            // file metadata anyway so the next poll does not re-detect rotation
            // against a stale checkpoint.
            if (fileInfo.Length <= offset)
            {
                _checkpointStore.UpdateCheckpoint(target.FilePath, target.TargetName, offset, fileInfo.Length, fileCreatedUtc);
                return Task.CompletedTask;
            }

            using var fs = new FileStream(target.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(offset, SeekOrigin.Begin);

            using var reader = new StreamReader(fs, Encoding.UTF8);
            var batch = new List<NginxLogRecord>(4096);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                NginxLogRecord? record = target.LogType switch
                {
                    NginxLogType.Access => NginxAccessLogParser.Parse(line),
                    NginxLogType.Error => NginxErrorLogParser.Parse(line),
                    _ => null
                };

                if (record is null)
                {
                    Interlocked.Increment(ref _parseErrors);
                    continue;
                }

                record.TargetName = target.TargetName;
                record.SourceFile = target.FilePath;
                record.HostName = _hostName;
                _lastRecordTimestamp = record.Timestamp;

                if (target.LogType == NginxLogType.Access)
                {
                    Interlocked.Increment(ref _accessRecordsRead);

                    // Skip static file requests
                    if (_targetOptions.ExcludeStaticFiles && IsStaticFile(record.Path))
                    {
                        Interlocked.Increment(ref _filteredStaticFiles);
                        continue;
                    }

                    // Skip by path prefix or remote address rules
                    if (_filterService.ShouldExclude(record.Path, record.RemoteAddress))
                    {
                        Interlocked.Increment(ref _filteredByRules);
                        continue;
                    }
                }
                else
                    Interlocked.Increment(ref _errorRecordsRead);

                batch.Add(record);

                // Flush in chunks to avoid unbounded memory growth
                if (batch.Count >= 4096)
                {
                    _sink.WriteBatch(batch);
                    batch.Clear();
                }
            }

            // Flush remaining records
            if (batch.Count > 0)
                _sink.WriteBatch(batch);

            _checkpointStore.UpdateCheckpoint(target.FilePath, target.TargetName, fs.Position, fileInfo.Length, fileCreatedUtc);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _readErrors);
            _logger.LogError(ex, "Error tailing {Path}", target.FilePath);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Determines the correct offset to start reading from, handling two rotation styles:
    ///
    /// 1. copytruncate: file is truncated in-place (same inode, smaller size).
    ///    Detected by: fileSize less than checkpointed offset.
    ///
    /// 2. rename/create: old file is renamed, new file created at same path (new inode).
    ///    Detected by: creation time newer than the checkpoint AND file size at or
    ///    below the previously seen size. The size check avoids false positives on
    ///    bind-mounted filesystems whose reported creation time can jitter on every
    ///    poll. Trade-off: if a brand-new file grows past the previous offset
    ///    between two polls, we will miss its prefix.
    /// </summary>
    private long ResolveOffset(TailTarget target, FileInfo fileInfo)
    {
        var cp = _checkpointStore.GetCheckpoint(target.FilePath);
        if (cp is null)
        {
            // First time we've seen this file. Start at the end when configured
            // so we don't backfill the whole existing file (only new lines ship).
            if (_discovery.StartAtEnd && fileInfo.Length > 0)
            {
                _logger.LogInformation("New file {Path}: starting at end (offset {Offset}) — backfill skipped",
                    target.FilePath, fileInfo.Length);
                return fileInfo.Length;
            }
            return 0;
        }

        var offset = cp.Offset;

        // copytruncate: file got smaller than our offset.
        if (fileInfo.Length < offset)
        {
            // Empty rotated files are common and not actionable on their own;
            // log at Debug to avoid spamming on idle log paths.
            if (fileInfo.Length == 0)
            {
                _logger.LogDebug("Rotation detected (copytruncate, empty) for {Path}: prior offset {Offset}",
                    target.FilePath, offset);
            }
            else
            {
                _logger.LogInformation("Rotation detected (copytruncate) for {Path}: file size {Size} < offset {Offset}",
                    target.FilePath, fileInfo.Length, offset);
            }
            Interlocked.Increment(ref _rotationsDetected);
            return 0;
        }

        // rename/create: only treat as rotation if creation time advanced AND
        // the file is no larger than what we last saw. Pure creation-time
        // changes are unreliable on container bind-mounts.
        if (cp.FileCreatedUtc is not null && fileInfo.Length <= cp.FileSize)
        {
            var currentCreatedUtc = GetFileCreationTimeUtc(target.FilePath);
            if (currentCreatedUtc is not null && currentCreatedUtc > cp.FileCreatedUtc.Value.AddSeconds(1))
            {
                _logger.LogInformation("Rotation detected (new file) for {Path}: created {NewCreated} vs checkpoint {OldCreated}",
                    target.FilePath, currentCreatedUtc, cp.FileCreatedUtc);
                Interlocked.Increment(ref _rotationsDetected);
                return 0;
            }
        }

        return offset;
    }

    public List<NginxLogRecord> ReadRecentLines(int maxLines = 200)
    {
        var allRecords = new List<NginxLogRecord>();
        var targets = GetAllTargets();

        foreach (var target in targets)
        {
            if (!File.Exists(target.FilePath)) continue;

            try
            {
                var lines = ReadTailLines(target.FilePath, maxLines);
                foreach (var line in lines)
                {
                    NginxLogRecord? record = target.LogType switch
                    {
                        NginxLogType.Access => Parsing.NginxAccessLogParser.Parse(line),
                        NginxLogType.Error => Parsing.NginxErrorLogParser.Parse(line),
                        _ => null
                    };

                    if (record is null) continue;

                    record.TargetName = target.TargetName;
                    record.SourceFile = target.FilePath;
                    record.HostName = _hostName;
                    allRecords.Add(record);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error reading tail of {Path}", target.FilePath);
            }
        }

        // Sort by timestamp descending, take most recent
        return allRecords
            .OrderByDescending(r => r.Timestamp)
            .Take(maxLines)
            .ToList();
    }

    private IReadOnlyList<TailTarget> GetAllTargets()
    {
        var targets = new List<TailTarget>();
        foreach (var t in GetConfiguredTargets().Where(t => t.Enabled))
        {
            if (!string.IsNullOrEmpty(t.AccessLogPath))
                targets.Add(new TailTarget { TargetName = t.Name, FilePath = t.AccessLogPath, LogType = NginxLogType.Access, Enabled = true });
            if (!string.IsNullOrEmpty(t.ErrorLogPath))
                targets.Add(new TailTarget { TargetName = t.Name, FilePath = t.ErrorLogPath, LogType = NginxLogType.Error, Enabled = true });
        }
        return targets;
    }

    private static List<string> ReadTailLines(string filePath, int maxLines)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length == 0) return new();

        // Read from the end in chunks to find last N lines
        const int chunkSize = 8192;
        var result = new List<string>();
        var buffer = new byte[chunkSize];
        var tail = new List<byte>();
        var pos = fs.Length;

        while (pos > 0 && result.Count < maxLines)
        {
            var readSize = (int)Math.Min(chunkSize, pos);
            pos -= readSize;
            fs.Seek(pos, SeekOrigin.Begin);
            var bytesRead = fs.Read(buffer, 0, readSize);

            // Prepend to tail
            var chunk = new byte[bytesRead + tail.Count];
            Array.Copy(buffer, 0, chunk, 0, bytesRead);
            tail.CopyTo(chunk, bytesRead);
            tail = new List<byte>(chunk);

            // Count lines
            var text = System.Text.Encoding.UTF8.GetString(chunk);
            var lines = text.Split('\n');
            result.Clear();
            foreach (var line in lines)
            {
                var trimmed = line.TrimEnd('\r');
                if (!string.IsNullOrWhiteSpace(trimmed))
                    result.Add(trimmed);
            }
        }

        // Return last maxLines
        if (result.Count > maxLines)
            result = result.Skip(result.Count - maxLines).ToList();

        return result;
    }

    private bool IsStaticFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        // Strip query string
        var qIdx = path.IndexOf('?');
        var cleanPath = qIdx >= 0 ? path[..qIdx] : path;

        var dotIdx = cleanPath.LastIndexOf('.');
        if (dotIdx < 0) return false;

        var ext = cleanPath[dotIdx..];
        return _targetOptions.ExcludeExtensions.Contains(ext);
    }

    private static DateTime? GetFileCreationTimeUtc(string path)
    {
        try
        {
            return File.GetCreationTimeUtc(path);
        }
        catch
        {
            return null;
        }
    }
}
