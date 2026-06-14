using System.Diagnostics;
using LogDB.Extensions.Logging;
using LogDB.Client.Models;
using com.logdb.windows.iis.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace com.logdb.windows.iis.Services;

/// <summary>
/// Background service that exports IIS W3C log files to LogDB.
/// Reads configuration from appsettings.json, reads log files, applies filters, and sends to LogDB.
/// </summary>
public class IISLogExportService : BackgroundService
{
    private readonly ILogger<IISLogExportService> _logger;
    private readonly ILogDBClient _logDBClient;
    private readonly IISLogReader _logReader;
    private readonly AzureAppServiceJsonReader _azureReader;
    private readonly IISLogFilter _logFilter;
    private readonly FileStateTracker _fileStateTracker;

    private readonly IISExportConfig _config;
    private readonly string _serverName;
    private readonly string _serverEnvironment;

    // When batching is on, each read-chunk is sent via SendLogBatchAsync in
    // sub-batches of _batchSize instead of one LogAsync per row. The file's
    // byte-position checkpoint is advanced only after every sub-batch in the
    // chunk has confirmed, so a send failure re-reads the chunk next cycle
    // (the deterministic per-row Guid makes that re-delivery idempotent).
    private readonly bool _enableBatching;
    private readonly int _batchSize;

    // Command-line flags (set from Program.cs)
    public static DateTime? InitialStartDate { get; set; }
    public static bool ResetState { get; set; }

    private bool _stateResetDone;

    // Store field mappings per file (headers) to handle resuming/chunking correctly
    private readonly Dictionary<string, Dictionary<string, int>> _fileHeaderMaps = new();

    // Max entries read per file per chunk. Bounds memory for very large log
    // files (multi-hundred-MB historical logs) and keeps a single huge file
    // from monopolizing the export cycle while other sites wait.
    private const int ReadChunkSize = 5000;

    public IISLogExportService(
        ILogger<IISLogExportService> logger,
        IConfiguration configuration,
        ILogDBClient logDBClient,
        IISLogReader logReader,
        AzureAppServiceJsonReader azureReader,
        IISLogFilter logFilter,
        FileStateTracker fileStateTracker)
    {
        _logger = logger;
        _logDBClient = logDBClient;
        _logReader = logReader;
        _azureReader = azureReader;
        _logFilter = logFilter;
        _fileStateTracker = fileStateTracker;

        _serverName = configuration["Server:ServerName"] ?? Environment.MachineName;
        _serverEnvironment = configuration["Server:ServerEnvironment"] ?? "Production";

        _enableBatching = configuration.GetValue("Batch:EnableBatching", false);
        _batchSize = Math.Max(1, configuration.GetValue("Batch:Size", 100));

        // Bind IIS config section
        _config = new IISExportConfig();
        configuration.GetSection("IIS").Bind(_config);

        if (_config.GetEffectiveSources().Count == 0)
        {
            _logger.LogWarning("No IIS log sources configured in appsettings.json - nothing to export");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IIS Log Export Service started");

        var sources = _config.GetEffectiveSources();
        if (sources.Count == 0)
        {
            _logger.LogError("No IIS log sources configured. Add LogSources or LogPaths to appsettings.json and restart.");
            return;
        }

        _logger.LogInformation("Monitoring {Count} log source(s), export interval: {Interval}m",
            sources.Count, _config.ExportIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessExportCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in export cycle");
            }

            await Task.Delay(TimeSpan.FromMinutes(_config.ExportIntervalMinutes), stoppingToken);
        }

        _logger.LogInformation("IIS Log Export Service stopped");
    }

    private async Task ProcessExportCycleAsync(CancellationToken stoppingToken)
    {
        _logger.LogDebug("Starting export cycle...");

        // Handle --reset flag (once)
        if (ResetState && !_stateResetDone)
        {
            _logger.LogInformation("Resetting all IIS exporter state");
            _fileStateTracker.ClearAll();
            _stateResetDone = true;
        }

        var totalEntriesProcessed = 0;
        var stopwatch = Stopwatch.StartNew();

        var sources = _config.GetEffectiveSources();

        foreach (var source in sources)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            var processed = source.Format switch
            {
                LogFormat.W3C => await ProcessW3CSourceAsync(source.Path, stoppingToken),
                LogFormat.AzureJson => await ProcessAzureJsonSourceAsync(source.Path, stoppingToken),
                _ => await ProcessAutoDetectSourceAsync(source.Path, stoppingToken)
            };

            totalEntriesProcessed += processed;
        }

        stopwatch.Stop();

        if (totalEntriesProcessed > 0)
        {
            _logger.LogInformation(
                "Export cycle: {Count} entries in {Elapsed}ms",
                totalEntriesProcessed, stopwatch.ElapsedMilliseconds);
        }
    }

    private async Task<int> ProcessW3CSourceAsync(string logPath, CancellationToken stoppingToken)
    {
        var logFiles = FilterByDirectory(_logReader.GetLogFiles(logPath));

        if (logFiles.Count == 0)
        {
            _logger.LogInformation("No W3C log files found in: {Path}", logPath);
            return 0;
        }

        _logger.LogInformation("W3C format - {Count} files in: {Path}", logFiles.Count, logPath);
        return await ProcessFilesAsync(logFiles, isAzureJson: false, stoppingToken);
    }

    private async Task<int> ProcessAzureJsonSourceAsync(string logPath, CancellationToken stoppingToken)
    {
        var logFiles = FilterByDirectory(_azureReader.GetJsonLogFiles(logPath, strictDetection: false));

        if (logFiles.Count == 0)
        {
            _logger.LogInformation("No Azure JSON log files found in: {Path}", logPath);
            return 0;
        }

        _logger.LogInformation("Azure JSON format - {Count} files in: {Path}", logFiles.Count, logPath);
        return await ProcessFilesAsync(logFiles, isAzureJson: true, stoppingToken);
    }

    private async Task<int> ProcessAutoDetectSourceAsync(string logPath, CancellationToken stoppingToken)
    {
        var totalProcessed = 0;

        // Try Azure JSON first (strict: only _d=/h=/m= structure, no flat scan)
        var azureJsonFiles = FilterByDirectory(_azureReader.GetJsonLogFiles(logPath, strictDetection: true));
        if (azureJsonFiles.Count > 0)
        {
            _logger.LogInformation("Auto-detected Azure JSON - {Count} files in: {Path}", azureJsonFiles.Count, logPath);
            totalProcessed += await ProcessFilesAsync(azureJsonFiles, isAzureJson: true, stoppingToken);
        }

        // Also try W3C (both can coexist in the same path)
        var w3cFiles = FilterByDirectory(_logReader.GetLogFiles(logPath));
        if (w3cFiles.Count > 0)
        {
            _logger.LogInformation("Auto-detected W3C - {Count} files in: {Path}", w3cFiles.Count, logPath);
            totalProcessed += await ProcessFilesAsync(w3cFiles, isAzureJson: false, stoppingToken);
        }

        if (azureJsonFiles.Count == 0 && w3cFiles.Count == 0)
        {
            _logger.LogInformation("No log files found in: {Path}", logPath);
        }

        return totalProcessed;
    }

    private async Task<int> ProcessFilesAsync(List<string> logFiles, bool isAzureJson, CancellationToken stoppingToken)
    {
        // Group files by their site folder (parent directory) and process the
        // sites round-robin instead of strictly alphabetically. Without this, a
        // site with a large historical backlog that sorts first (e.g. FTPSVC*)
        // monopolizes the entire cycle and later sites (W3SVC*) are never
        // reached, so their logs never leave the box. Files within a site keep
        // their original (date) order, which IIS rotation relies on.
        var siteQueues = logFiles
            .GroupBy(f => Path.GetDirectoryName(f) ?? string.Empty)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Queue<string>(g.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        var totalProcessed = 0;

        // Each round takes the next pending file from every site, so all sites
        // advance together within a single cycle rather than one-site-at-a-time.
        while (siteQueues.Any(q => q.Count > 0))
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            foreach (var queue in siteQueues)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                if (queue.Count == 0)
                    continue;

                var logFile = queue.Dequeue();
                totalProcessed += await ProcessLogFileAsync(logFile, isAzureJson, stoppingToken);
            }
        }

        return totalProcessed;
    }

    private async Task<int> ProcessLogFileAsync(string logFile, bool isAzureJson, CancellationToken stoppingToken)
    {
        var fileName = Path.GetFileName(logFile);

        // Check if file needs processing using size/mtime comparison
        var (needsProcessing, lastBytePosition, lastLogTimestamp) = _fileStateTracker.CheckFile(logFile);

        if (!needsProcessing)
        {
            _logger.LogTrace("Skipping unchanged file: {File}", fileName);
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"  {fileName}");
        Console.ResetColor();
        if (lastBytePosition > 0)
            Console.Write($" (resuming from byte {lastBytePosition:N0})");
        Console.WriteLine();

        long currentPosition = lastBytePosition;
        var runningTimestamp = lastLogTimestamp;
        var totalRead = 0;
        var totalSent = 0;

        // Read the file in bounded chunks. This caps memory for very large files
        // (a 500 MB log no longer materializes as one giant list) and persists
        // progress after every chunk, so a crash/restart resumes mid-file.
        while (!stoppingToken.IsCancellationRequested)
        {
            List<IISLogEntry> entries;
            long newBytePosition;

            if (isAzureJson)
            {
                (entries, newBytePosition, _) = await _azureReader.ReadEntriesAsync(
                    logFile, currentPosition, ReadChunkSize, null, stoppingToken);
            }
            else
            {
                _fileHeaderMaps.TryGetValue(logFile, out var fieldMap);

                Dictionary<string, int> updatedFieldMap;
                (entries, newBytePosition, updatedFieldMap) = await _logReader.ReadEntriesAsync(
                    logFile, currentPosition, ReadChunkSize, fieldMap, stoppingToken);

                if (updatedFieldMap != null && updatedFieldMap.Count > 0)
                    _fileHeaderMaps[logFile] = updatedFieldMap;
            }

            var readCount = entries.Count;

            if (readCount == 0)
            {
                // EOF with nothing new - persist position and stop.
                _fileStateTracker.UpdateFileState(logFile, newBytePosition, runningTimestamp);
                break;
            }

            // Track max timestamp over the raw (unfiltered) chunk for state.
            var chunkMaxTimestamp = entries.Max(e => e.Timestamp);
            if (!runningTimestamp.HasValue || chunkMaxTimestamp > runningTimestamp.Value)
                runningTimestamp = chunkMaxTimestamp;

            // Migration safety: only on the first chunk when resuming from byte 0.
            if (currentPosition == 0 && lastLogTimestamp.HasValue)
                entries = entries.Where(e => e.Timestamp > lastLogTimestamp.Value).ToList();

            // InitialStartDate filter (first run only)
            if (InitialStartDate.HasValue)
                entries = entries.Where(e => e.Timestamp >= InitialStartDate.Value).ToList();

            // Apply filters from config
            var filteredEntries = _logFilter.FilterEntries(entries, _config);

            if (filteredEntries.Count > 0)
            {
                Console.Write("  ");
                var delivered = await SendToLogDBAsync(filteredEntries, stoppingToken);
                Console.WriteLine();
                if (!delivered)
                {
                    // Batched send failed — leave the byte position unadvanced
                    // so this chunk is re-read next cycle. Deterministic per-row
                    // Guids make the re-delivery idempotent.
                    _logger.LogWarning(
                        "IIS: deferring {File} — batch send failed; chunk will retry next cycle", fileName);
                    break;
                }
            }

            currentPosition = newBytePosition;
            _fileStateTracker.UpdateFileState(logFile, currentPosition, runningTimestamp);

            totalRead += readCount;
            totalSent += filteredEntries.Count;

            // A short read means we hit EOF; this file is drained for now.
            if (readCount < ReadChunkSize)
                break;
        }

        if (totalRead > 0)
        {
            var siteFolder = Path.GetFileName(Path.GetDirectoryName(logFile)) ?? "?";
            Console.WriteLine($"  Site {siteFolder} / {fileName}: read {totalRead} | sent {totalSent}");
            // Only surface per-file scans that actually shipped rows. A reset or a
            // future start date makes IIS walk every historical file reading-but-
            // sending-nothing; at Information that floods the live console. The
            // "read N, sent 0" case drops to Debug so it stays in the file sink only.
            if (totalSent > 0)
                _logger.LogInformation("Site {Site} file {File}: read {Read}, sent {Sent}",
                    siteFolder, fileName, totalRead, totalSent);
            else
                _logger.LogDebug("Site {Site} file {File}: read {Read}, sent 0 (all filtered)",
                    siteFolder, fileName, totalRead);
        }

        return totalSent;
    }

    /// <summary>
    /// Sends a chunk of filtered IIS entries to LogDB. Returns <c>true</c> when
    /// the caller may advance the file's byte-position checkpoint. The per-row
    /// path always returns <c>true</c> (best-effort, matches historical
    /// behavior); the batched path returns <c>false</c> if a sub-batch failed so
    /// the chunk is re-read next cycle.
    /// </summary>
    private async Task<bool> SendToLogDBAsync(List<IISLogEntry> entries, CancellationToken stoppingToken)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();

        if (_enableBatching)
        {
            return await SendBatchedAsync(entries, md5, stoppingToken);
        }

        int sent = 0;

        foreach (var entry in entries)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            try
            {
                var log = BuildLog(entry, md5);

                await _logDBClient.LogAsync(log);
                sent++;

                LogRowDiagnostic(entry);

                if (sent % 10 == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("X");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send log entry to LogDB");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("E");
                Console.ResetColor();
            }
        }

        await _logDBClient.FlushAsync();
        return true;
    }

    /// <summary>
    /// Batched send: groups the chunk into _batchSize sub-batches and ships each
    /// via a single SendLogBatchAsync call. Returns false on the first failed
    /// sub-batch (already-sent sub-batches are idempotent thanks to the
    /// deterministic per-row Guid, so re-reading the chunk next cycle is safe).
    /// </summary>
    private async Task<bool> SendBatchedAsync(
        List<IISLogEntry> entries,
        System.Security.Cryptography.MD5 md5,
        CancellationToken stoppingToken)
    {
        var pairs = new List<(IISLogEntry Entry, global::LogDB.Client.Models.Log Log)>(entries.Count);
        foreach (var entry in entries)
        {
            if (stoppingToken.IsCancellationRequested)
                return false;
            pairs.Add((entry, BuildLog(entry, md5)));
        }

        foreach (var chunk in pairs.Chunk(_batchSize))
        {
            if (stoppingToken.IsCancellationRequested)
                return false;

            var logs = chunk.Select(p => p.Log).ToList();

            try
            {
                var status = await _logDBClient.SendLogBatchAsync(logs, stoppingToken);
                if (status != LogResponseStatus.Success)
                {
                    _logger.LogWarning(
                        "IIS batch send returned {Status} for {Count} row(s) — chunk will retry next cycle",
                        status, logs.Count);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "IIS batch send threw for {Count} row(s) — chunk will retry next cycle", logs.Count);
                return false;
            }

            // Batch confirmed: emit the per-row Online Console lines now, so a
            // logged "► [IIS]" line still means the row was actually delivered.
            foreach (var (entry, _) in chunk)
            {
                LogRowDiagnostic(entry);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("X");
            Console.ResetColor();
        }

        return true;
    }

    private global::LogDB.Client.Models.Log BuildLog(IISLogEntry entry, System.Security.Cryptography.MD5 md5)
    {
        var iisEvent = new LogIISEvent
        {
            Guid = GenerateDeterministicId(entry, md5),
            Timestamp = entry.Timestamp,
            Collection = GetCollectionName(entry),
            Method = entry.Method,
            UriStem = entry.UriStem,
            UriQuery = entry.UriQuery,
            Port = entry.ServerPort > 0 ? entry.ServerPort : null,
            Username = entry.Username,
            Host = entry.Host,
            ClientIp = entry.ClientIp,
            ServerIp = entry.ServerIp,
            UserAgent = entry.UserAgent,
            Referer = entry.Referer,
            Status = entry.StatusCode,
            SubStatus = entry.SubStatus > 0 ? entry.SubStatus : null,
            Win32Status = entry.Win32Status > 0 ? entry.Win32Status : null,
            TimeTaken = entry.TimeTaken > 0 ? (int)entry.TimeTaken : null,
            BytesSent = entry.BytesSent > 0 ? entry.BytesSent : null,
            BytesReceived = entry.BytesReceived > 0 ? entry.BytesReceived : null,
            SiteName = entry.SiteName,
            // LogIISEvent.ServerName always carries the configured Server:ServerName —
            // matches the working Metrics pattern. The mapper rewrites the key when
            // the user typed any per-module override, so the override flows here
            // without needing a separate signal key. In the no-override case the
            // value defaults to Environment.MachineName, which equals the W3C
            // log's s-computername for local IIS — bit-for-bit identical to the
            // pre-1.1.15 path.
            ServerName = _serverName,
        };

        var log = iisEvent.ToLog();

        // Override fields not handled by the typed model
        log.Application = _config.ApplicationName;
        log.Environment = _serverEnvironment;
        log.Level = MapStatusToLogLevel(entry.StatusCode);
        log.Message = FormatLogMessage(entry);
        log.Source = entry.SiteName ?? "IIS";
        foreach (var lbl in _config.Labels)
            if (!log.Label.Contains(lbl)) log.Label.Add(lbl);

        // Extra attributes not covered by LogIISEvent
        if (!string.IsNullOrEmpty(entry.ProtocolVersion))
            log.AttributesS["protocol"] = entry.ProtocolVersion;
        if (!string.IsNullOrEmpty(entry.SourceFile))
            log.AttributesS["sourceFile"] = Path.GetFileName(entry.SourceFile);
        if (entry.LineNumber > 0)
            log.AttributesN["lineNumber"] = entry.LineNumber;

        // Preserve the raw W3C s-computername on the row whenever it
        // differs from the configured ServerName — keeps it queryable
        // after an override (or in UNC-path multi-server collection).
        if (!string.IsNullOrEmpty(entry.ServerName)
            && !string.Equals(entry.ServerName, _serverName, StringComparison.OrdinalIgnoreCase))
        {
            log.AttributesS["original_server_name"] = entry.ServerName;
        }

        foreach (var kvp in entry.AdditionalFields)
        {
            log.AttributesS[kvp.Key] = kvp.Value;
        }

        return log;
    }

    private void LogRowDiagnostic(IISLogEntry entry)
    {
        // "LogEventTimestamp" is a well-known scope key the LogDB collector
        // reads to surface the record's own timestamp in the Online Console
        // (separate from when this line is logged). Only attach it when the
        // W3C date/time actually parsed.
        IDisposable? eventTimeScope = entry.Timestamp != default
            ? _logger.BeginScope(new Dictionary<string, object>
            {
                ["LogEventTimestamp"] = entry.Timestamp
            })
            : null;
        try
        {
            _logger.LogInformation("► [IIS] {Method} {Uri} -> {Status} ({TimeTaken}ms)",
                entry.Method, entry.UriStem, entry.StatusCode, entry.TimeTaken);
        }
        finally
        {
            eventTimeScope?.Dispose();
        }
    }

    private global::LogDB.Client.Models.LogLevel MapStatusToLogLevel(int statusCode)
    {
        return statusCode switch
        {
            >= 500 => global::LogDB.Client.Models.LogLevel.Error,
            >= 400 => global::LogDB.Client.Models.LogLevel.Warning,
            >= 300 => global::LogDB.Client.Models.LogLevel.Info,
            >= 200 => global::LogDB.Client.Models.LogLevel.Info,
            _ => global::LogDB.Client.Models.LogLevel.Debug
        };
    }

    private string? GetCollectionName(IISLogEntry entry)
    {
        // 1. Collection map by site name (most specific)
        if (_config.CollectionMap?.Any() == true && !string.IsNullOrEmpty(entry.SiteName))
        {
            if (_config.CollectionMap.TryGetValue(entry.SiteName, out var mappedCollection))
                return mappedCollection;
        }

        // 2. Explicit collection from config
        if (!string.IsNullOrEmpty(_config.Collection))
            return _config.Collection;

        // 3. Auto-generate from source file path
        if (!string.IsNullOrEmpty(entry.SourceFile))
        {
            var meaningful = GetMeaningfulDirectoryName(entry.SourceFile);
            if (!string.IsNullOrEmpty(meaningful))
                return $"iis-logs-{meaningful.ToLowerInvariant()}";
        }

        return "iis-events";
    }

    /// <summary>
    /// Extract meaningful directory name from a log file path.
    /// W3C: parent folder (e.g. W3SVC1 from ...\W3SVC1\u_ex250219.log)
    /// Azure JSON: walks up past time-partition segments (m=XX, h=XX, *_d=XX)
    ///   and extracts the prefix before _d= (e.g. "appservice" from appservice_d=19/h=14/m=30/PT1H.json)
    /// </summary>
    private static string? GetMeaningfulDirectoryName(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);

        while (!string.IsNullOrEmpty(dir))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name))
                break;

            // Azure minute segment: m=00, m=15, m=30, m=45
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^m=\d{2}$"))
            {
                dir = Path.GetDirectoryName(dir);
                continue;
            }

            // Azure hour segment: h=00 .. h=23
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^h=\d{2}$"))
            {
                dir = Path.GetDirectoryName(dir);
                continue;
            }

            // Azure day segment: something_d=01 .. something_d=31
            // Extract the prefix before _d= as the meaningful name
            var dayMatch = System.Text.RegularExpressions.Regex.Match(name, @"^(.+?)_d=\d{2}$");
            if (dayMatch.Success)
                return dayMatch.Groups[1].Value;

            // Not an Azure time-partition segment - this is the meaningful directory
            return name;
        }

        return null;
    }

    private string FormatLogMessage(IISLogEntry entry)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(entry.Method))
            parts.Add(entry.Method);
        if (!string.IsNullOrEmpty(entry.UriStem))
            parts.Add(entry.UriStem);

        parts.Add($"-> {entry.StatusCode}");

        if (entry.TimeTaken > 0)
            parts.Add($"({entry.TimeTaken}ms)");

        return string.Join(" ", parts);
    }

    private Dictionary<string, string> BuildStringAttributes(IISLogEntry entry)
    {
        var attrs = new Dictionary<string, string>();

        if (!string.IsNullOrEmpty(entry.Method))
            attrs["method"] = entry.Method;
        if (!string.IsNullOrEmpty(entry.UriStem))
            attrs["uriStem"] = entry.UriStem;
        if (!string.IsNullOrEmpty(entry.UriQuery))
            attrs["queryString"] = entry.UriQuery;
        if (!string.IsNullOrEmpty(entry.ClientIp))
            attrs["clientIp"] = entry.ClientIp;
        if (!string.IsNullOrEmpty(entry.ServerIp))
            attrs["serverIp"] = entry.ServerIp;
        if (!string.IsNullOrEmpty(entry.UserAgent))
            attrs["userAgent"] = entry.UserAgent;
        if (!string.IsNullOrEmpty(entry.Referer))
            attrs["referer"] = entry.Referer;
        if (!string.IsNullOrEmpty(entry.Username))
            attrs["username"] = entry.Username;
        if (!string.IsNullOrEmpty(entry.Host))
            attrs["host"] = entry.Host;
        if (!string.IsNullOrEmpty(entry.SiteName))
            attrs["siteName"] = entry.SiteName;
        if (!string.IsNullOrEmpty(entry.ServerName))
            attrs["serverName"] = entry.ServerName;
        if (!string.IsNullOrEmpty(entry.ProtocolVersion))
            attrs["protocol"] = entry.ProtocolVersion;
        if (!string.IsNullOrEmpty(entry.SourceFile))
            attrs["sourceFile"] = Path.GetFileName(entry.SourceFile);

        foreach (var kvp in entry.AdditionalFields)
        {
            attrs[kvp.Key] = kvp.Value;
        }

        attrs["_sys_type"] = "iis_event";

        return attrs;
    }

    private Dictionary<string, double> BuildNumericAttributes(IISLogEntry entry)
    {
        var attrs = new Dictionary<string, double>
        {
            ["statusCode"] = entry.StatusCode,
            ["timeTaken"] = entry.TimeTaken
        };

        if (entry.ServerPort > 0)
            attrs["serverPort"] = entry.ServerPort;
        if (entry.SubStatus > 0)
            attrs["subStatus"] = entry.SubStatus;
        if (entry.Win32Status > 0)
            attrs["win32Status"] = entry.Win32Status;
        if (entry.BytesSent > 0)
            attrs["bytesSent"] = entry.BytesSent;
        if (entry.BytesReceived > 0)
            attrs["bytesReceived"] = entry.BytesReceived;
        if (entry.LineNumber > 0)
            attrs["lineNumber"] = entry.LineNumber;

        return attrs;
    }

    private List<string> FilterByDirectory(List<string> files)
    {
        var hasInclude = _config.IncludeDirectories?.Any() == true;
        var hasExclude = _config.ExcludeDirectories?.Any() == true;

        if (!hasInclude && !hasExclude)
            return files;

        return files.Where(f =>
        {
            var dirName = Path.GetFileName(Path.GetDirectoryName(f)) ?? "";

            if (hasInclude && !_config.IncludeDirectories!.Any(d =>
                d.Equals(dirName, StringComparison.OrdinalIgnoreCase)))
                return false;

            if (hasExclude && _config.ExcludeDirectories!.Any(d =>
                d.Equals(dirName, StringComparison.OrdinalIgnoreCase)))
                return false;

            return true;
        }).ToList();
    }

    private static string GenerateDeterministicId(IISLogEntry entry, System.Security.Cryptography.MD5 md5)
    {
        // Derive the ID from the entry's stable content rather than its line
        // number. LineNumber is relative to each (chunked/resumed) read, so it
        // repeats across chunks - making the "deterministic" ID neither unique
        // per row nor stable across re-processing. A content hash is both.
        //  (unit separator) avoids accidental collisions between adjacent
        // fields (e.g. "a" + "bc" vs "ab" + "c").
        const char sep = '';
        var input = string.Join(sep,
            entry.SourceFile,
            entry.Timestamp.Ticks,
            entry.Method,
            entry.UriStem,
            entry.UriQuery,
            entry.ClientIp,
            entry.Username,
            entry.StatusCode,
            entry.SubStatus,
            entry.TimeTaken,
            entry.BytesSent,
            entry.BytesReceived);
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return new Guid(hash).ToString();
    }
}
