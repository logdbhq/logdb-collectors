using System.Text.Json;
using com.logdb.nginx.collector.Configuration;
using com.logdb.nginx.collector.Models;
using LogDB.Client.Models;
using LogDB.Extensions.Logging;
using Microsoft.Extensions.Options;
using LogDbLogLevel = LogDB.Client.Models.LogLevel;

namespace com.logdb.nginx.collector.Services;

public class LogDbExporterService : ILogDbExporter
{
    private readonly ILogger<LogDbExporterService> _logger;
    private readonly LogDbExporterOptions _options;
    private readonly ILogDBClient _client;
    private readonly ExporterConsoleBuffer _console;
    private readonly DeliveryConsoleBuffer _delivery;
    private readonly DeliveryActivityTracker _activity;
    private readonly string _settingsPath;

    private const int MaxSentMessageLength = 512;

    private long _batchesSent;
    private long _recordsSent;
    private long _sendErrors;
    private long _retryCount;
    private DateTime? _lastSendUtc;
    private string? _lastError;
    private bool _healthy = true;
    private bool _enabledOverride;
    private bool _hasOverride;
    private int _flushIntervalSeconds;
    private DateTime _lastErrorLogUtc;

    private const int FlushIntervalMinSeconds = 1;
    private const int FlushIntervalMaxSeconds = 300;

    private static readonly HashSet<string> PlaceholderApiKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "your-api-key-here",
        "your-api-key",
        "your-key",
        "YOUR_API_KEY_HERE",
        "YOUR_API_KEY",
        "<api-key>",
        "REPLACE_ME"
    };

    private static bool IsApiKeyValid(string? apiKey) =>
        !string.IsNullOrWhiteSpace(apiKey) && !PlaceholderApiKeys.Contains(apiKey.Trim());

    public LogDbExporterService(ILogger<LogDbExporterService> logger, IOptions<LogDbExporterOptions> options,
        IOptions<CheckpointOptions> checkpointOptions, ILoggerFactory loggerFactory,
        ExporterConsoleBuffer console, DeliveryConsoleBuffer delivery, DeliveryActivityTracker activity)
    {
        _logger = logger;
        _options = options.Value;
        _console = console;
        _delivery = delivery;
        _activity = activity;

        var checkpointDir = Path.GetDirectoryName(Path.GetFullPath(checkpointOptions.Value.FilePath)) ?? ".";
        _settingsPath = Path.Combine(checkpointDir, "exporter-settings.json");
        _flushIntervalSeconds = ClampFlushInterval(_options.FlushIntervalSeconds);
        LoadSettings();

        var protocol = Enum.TryParse<LogDBProtocol>(_options.Protocol, ignoreCase: true, out var parsedProtocol)
            ? parsedProtocol
            : LogDBProtocol.Native;

        var clientOptions = new LogDBLoggerOptions
        {
            ApiKey = _options.ApiKey,
            ServiceUrl = _options.Endpoint,
            Protocol = protocol,
            EnableBatching = true,
            BatchSize = _options.MaxBatchRecords,
            FlushInterval = TimeSpan.FromSeconds(Math.Max(1, _options.FlushIntervalSeconds)),
            EnableCompression = _options.EnableCompression,
            MaxRetries = Math.Max(0, _options.MaxRetries),
            EnableCircuitBreaker = true
        };

        _client = new LogDBClient(Options.Create(clientOptions), loggerFactory.CreateLogger<LogDBClient>());
    }

    private bool IsEnabled => _hasOverride ? _enabledOverride : _options.Enabled;

    public void SetEnabled(bool enabled)
    {
        _enabledOverride = enabled;
        _hasOverride = true;
        SaveSettings();
        _logger.LogInformation("Exporter {State} via UI", enabled ? "enabled" : "disabled");
    }

    public int FlushIntervalSeconds => _flushIntervalSeconds;

    public void SetFlushIntervalSeconds(int seconds)
    {
        var clamped = ClampFlushInterval(seconds);
        if (clamped == _flushIntervalSeconds) return;
        _flushIntervalSeconds = clamped;
        SaveSettings();
        _logger.LogInformation("Exporter flush interval set to {Seconds}s via UI", clamped);
    }

    private static int ClampFlushInterval(int seconds) =>
        Math.Clamp(seconds, FlushIntervalMinSeconds, FlushIntervalMaxSeconds);

    public ExporterStatus GetStatus()
    {
        return new ExporterStatus
        {
            Enabled = IsEnabled,
            Endpoint = _options.Endpoint,
            ApiKeyConfigured = IsApiKeyValid(_options.ApiKey),
            Healthy = _healthy,
            BatchesSent = Interlocked.Read(ref _batchesSent),
            RecordsSent = Interlocked.Read(ref _recordsSent),
            SendErrors = Interlocked.Read(ref _sendErrors),
            RetryCount = Interlocked.Read(ref _retryCount),
            LastSendUtc = _lastSendUtc,
            LastError = _lastError,
            FlushIntervalSeconds = _flushIntervalSeconds,
            FlushIntervalMinSeconds = FlushIntervalMinSeconds,
            FlushIntervalMaxSeconds = FlushIntervalMaxSeconds
        };
    }

    public async Task<bool> SendBatchAsync(List<NginxLogRecord> batch, CancellationToken ct = default)
    {
        if (!IsEnabled || batch.Count == 0) return false;

        var startUtc = DateTime.UtcNow;
        var swStart = Environment.TickCount64;
        var apiKeyPrefix = ApiKeyPrefix(_options.ApiKey);

        if (!IsApiKeyValid(_options.ApiKey))
        {
            Interlocked.Increment(ref _sendErrors);
            var msg = string.IsNullOrWhiteSpace(_options.ApiKey)
                ? "API key is not configured (LOGDB_EXPORTER_APIKEY is empty)"
                : "API key is the placeholder value - set LOGDB_EXPORTER_APIKEY to your real key";
            _lastError = msg;
            _healthy = false;
            if ((DateTime.UtcNow - _lastErrorLogUtc).TotalSeconds >= 30)
            {
                _logger.LogError("Skipping export: {Msg}", msg);
                _lastErrorLogUtc = DateTime.UtcNow;
            }
            _console.Record(new ExporterCallEntry
            {
                Timestamp = startUtc,
                Outcome = "skipped",
                RecordCount = batch.Count,
                EventCount = 0,
                Endpoint = _options.Endpoint,
                ApiKeyPrefix = apiKeyPrefix,
                DurationMs = Environment.TickCount64 - swStart,
                Status = "api-key-invalid",
                Error = msg
            });
            foreach (var a in AggregateBatch(batch))
                _delivery.Record(BuildSentEntry(a.Record, a.Count, guid: "", "skipped", startUtc, apiKeyPrefix, "api-key-invalid", msg));
            _activity.Record(startUtc, batch.Count, "skipped");
            return false;
        }

        // Built before the try so the catch block can mark the same records as failed.
        var prepared = new List<(NginxLogRecord Record, int Count, Log Log)>();

        // Hard deadline on the whole send. Without this, a hung/unresponsive
        // grpc-logger makes FlushAsync block forever and wedges the replay worker,
        // so capture keeps running but nothing is ever delivered. On timeout we
        // fail the batch (recorded below) and the worker retries next cycle.
        var timeoutSeconds = Math.Max(10, _options.RequestTimeoutSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var sendCt = timeoutCts.Token;

        try
        {
            foreach (var a in AggregateBatch(batch))
                prepared.Add((a.Record, a.Count, MapToLogDbEvent(a.Record, a.Count)));

            int retries = 0;
            foreach (var item in prepared)
            {
                var result = await _client.LogAsync(item.Log, sendCt);
                if (result != LogResponseStatus.Success)
                {
                    Interlocked.Increment(ref _retryCount);
                    retries++;
                }
            }

            await _client.FlushAsync(sendCt);

            Interlocked.Increment(ref _batchesSent);
            Interlocked.Add(ref _recordsSent, batch.Count);
            _lastSendUtc = DateTime.UtcNow;
            _lastError = null;
            _healthy = true;

            _logger.LogDebug("Exported batch: {Raw} records -> {Aggregated} events", batch.Count, prepared.Count);

            var status = retries > 0 ? $"ok ({retries} retried)" : "ok";
            _console.Record(new ExporterCallEntry
            {
                Timestamp = startUtc,
                Outcome = "success",
                RecordCount = batch.Count,
                EventCount = prepared.Count,
                Endpoint = _options.Endpoint,
                ApiKeyPrefix = apiKeyPrefix,
                DurationMs = Environment.TickCount64 - swStart,
                Status = status
            });
            foreach (var item in prepared)
                _delivery.Record(BuildSentEntry(item.Record, item.Count, item.Log.Guid ?? "", "delivered", startUtc, apiKeyPrefix, status, error: null));
            _activity.Record(startUtc, batch.Count, "delivered");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuine shutdown (not our timeout) — let the worker exit quietly,
            // leave the batch uncommitted so it's retried on next start.
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _sendErrors);

            // Distinguish "grpc-logger didn't respond in time" from other errors so
            // the operator can see the pipeline isn't wedged — it's the backend.
            var isTimeout = ex is OperationCanceledException && timeoutCts.IsCancellationRequested;
            var statusLabel = isTimeout ? "Timeout" : ex.GetType().Name;
            var errorMsg = isTimeout
                ? $"Send timed out after {timeoutSeconds}s — grpc-logger unreachable or unresponsive ({_options.Endpoint})"
                : ex.Message;

            _lastError = errorMsg;
            _healthy = false;

            if ((DateTime.UtcNow - _lastErrorLogUtc).TotalSeconds >= 30)
            {
                _logger.LogError("Export failed ({Count} records): {Msg}", batch.Count, errorMsg);
                _lastErrorLogUtc = DateTime.UtcNow;
            }
            _console.Record(new ExporterCallEntry
            {
                Timestamp = startUtc,
                Outcome = "failed",
                RecordCount = batch.Count,
                EventCount = 0,
                Endpoint = _options.Endpoint,
                ApiKeyPrefix = apiKeyPrefix,
                DurationMs = Environment.TickCount64 - swStart,
                Status = statusLabel,
                Error = errorMsg
            });

            // If mapping failed before any record was prepared, fall back to the raw batch
            // so the operator still sees what was lost.
            var failedRecords = prepared.Count > 0
                ? prepared.Select(p => (p.Record, p.Count, Guid: p.Log.Guid ?? ""))
                : batch.Select(r => (Record: r, Count: 1, Guid: ""));
            foreach (var (record, count, guid) in failedRecords)
                _delivery.Record(BuildSentEntry(record, count, guid, "failed", startUtc, apiKeyPrefix, statusLabel, errorMsg));
            _activity.Record(startUtc, batch.Count, "failed");
            return false;
        }
    }

    private SentRecordEntry BuildSentEntry(NginxLogRecord r, int count, string guid, string outcome,
        DateTime sentUtc, string apiKeyPrefix, string? status, string? error)
    {
        var message = r.Message;
        if (message is not null && message.Length > MaxSentMessageLength)
            message = message[..MaxSentMessageLength] + "...";

        return new SentRecordEntry
        {
            SentUtc = sentUtc,
            RecordTimestamp = r.Timestamp,
            Outcome = outcome,
            AggregatedCount = count,
            LogType = r.LogType.ToString().ToLowerInvariant(),
            Target = r.TargetName,
            Host = r.HostName,
            SourceFile = r.SourceFile,
            Method = r.Method,
            Path = r.Path,
            Protocol = r.Protocol,
            StatusCode = r.StatusCode,
            ResponseBytes = r.ResponseBytes,
            RemoteAddress = r.RemoteAddress,
            Referer = r.Referer,
            UserAgent = r.UserAgent,
            RequestTime = r.RequestTime,
            Severity = r.Severity,
            Message = message,
            Guid = guid,
            Endpoint = _options.Endpoint,
            ApiKeyPrefix = apiKeyPrefix,
            Status = status,
            Error = error
        };
    }

    private static string ApiKeyPrefix(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return "(empty)";
        var trimmed = apiKey.Trim();
        if (PlaceholderApiKeys.Contains(trimmed)) return "(placeholder)";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(trimmed));
        return "fp:" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    private static List<AggregatedRecord> AggregateBatch(List<NginxLogRecord> batch)
    {
        var result = new List<AggregatedRecord>();

        // Group access logs by (second, method, path, status, remoteAddress, target)
        // Error logs are never aggregated - each one is unique/important
        var accessRecords = new List<NginxLogRecord>();
        var errorRecords = new List<NginxLogRecord>();

        foreach (var r in batch)
        {
            if (r.LogType == NginxLogType.Access)
                accessRecords.Add(r);
            else
                errorRecords.Add(r);
        }

        // Aggregate access logs
        var groups = accessRecords.GroupBy(r => (
            Second: new DateTime(r.Timestamp.Year, r.Timestamp.Month, r.Timestamp.Day,
                r.Timestamp.Hour, r.Timestamp.Minute, r.Timestamp.Second, r.Timestamp.Kind),
            r.Method,
            r.Path,
            r.StatusCode,
            r.RemoteAddress,
            r.TargetName
        ));

        foreach (var group in groups)
        {
            var representative = group.First();
            var count = group.Count();

            // Sum response bytes across the group
            if (count > 1)
            {
                var totalBytes = group.Where(r => r.ResponseBytes.HasValue).Sum(r => r.ResponseBytes!.Value);
                representative.ResponseBytes = totalBytes;
            }

            result.Add(new AggregatedRecord { Record = representative, Count = count });
        }

        // Error logs: no aggregation
        foreach (var r in errorRecords)
            result.Add(new AggregatedRecord { Record = r, Count = 1 });

        return result;
    }

    private class AggregatedRecord
    {
        public NginxLogRecord Record { get; set; } = null!;
        public int Count { get; set; }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var json = File.ReadAllText(_settingsPath);
            var state = JsonSerializer.Deserialize<ExporterSettings>(json);
            if (state is null) return;
            _enabledOverride = state.Enabled;
            _hasOverride = true;
            if (state.FlushIntervalSeconds is int s && s > 0)
                _flushIntervalSeconds = ClampFlushInterval(s);
            _logger.LogDebug("Exporter settings loaded: enabled={Enabled}, flushIntervalSeconds={Seconds}",
                state.Enabled, _flushIntervalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to load exporter settings: {Msg}", ex.Message);
        }
    }

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath);
            if (dir is not null) Directory.CreateDirectory(dir);
            var settings = new ExporterSettings
            {
                Enabled = _enabledOverride,
                FlushIntervalSeconds = _flushIntervalSeconds
            };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to save exporter settings: {Msg}", ex.Message);
        }
    }

    private class ExporterSettings
    {
        public bool Enabled { get; set; }
        public int? FlushIntervalSeconds { get; set; }
    }

    private Log MapToLogDbEvent(NginxLogRecord r, int count = 1)
    {
        var message = count > 1 ? $"[x{count}] {r.Message}" : r.Message;
        var logType = r.LogType.ToString().ToLowerInvariant();

        var evt = new LogNginxEvent
        {
            Timestamp = r.Timestamp,
            Collection = r.TargetName,
            LogType = logType,
            TargetName = r.TargetName,
            HostName = r.HostName,
            SourceFile = r.SourceFile,
            Message = message,
            Level = MapLogLevelString(r),
            RemoteAddress = r.RemoteAddress,
            Method = r.Method,
            Path = r.Path,
            Protocol = r.Protocol,
            StatusCode = r.StatusCode,
            ResponseBytes = r.ResponseBytes,
            Referer = r.Referer,
            UserAgent = r.UserAgent,
            RequestTime = r.RequestTime,
            ServerName = r.ServerName,
            Severity = r.Severity,
            Pid = r.Pid,
            Tid = r.Tid,
            ConnectionId = r.ConnectionId,
            Upstream = r.Upstream
        };

        var log = evt.ToLog();

        // Aggregation count
        if (count > 1)
            log.AttributesN["count"] = count;

        // Extra labels
        log.Label.Add(logType);
        if (r.LogType == NginxLogType.Error) log.Label.Add("error");

        return log;
    }

    private static string MapLogLevelString(NginxLogRecord r)
    {
        if (r.LogType == NginxLogType.Error)
        {
            return r.Severity?.ToLowerInvariant() switch
            {
                "emerg" or "alert" or "crit" => "Critical",
                "error" => "Error",
                "warn" => "Warning",
                "notice" or "info" => "Info",
                "debug" => "Debug",
                _ => "Error"
            };
        }

        return r.StatusCode switch
        {
            >= 500 => "Error",
            >= 400 => "Warning",
            _ => "Info"
        };
    }
}
