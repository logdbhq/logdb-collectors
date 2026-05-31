using System.Text.Json;
using com.logdb.docker.collector.Configuration;
using com.logdb.docker.collector.Models;
using LogDB.Client.Models;
using LogDB.Extensions.Logging;
using Microsoft.Extensions.Options;
using LogDbLogLevel = LogDB.Client.Models.LogLevel;

namespace com.logdb.docker.collector.Services;

public class LogDbExporterService : ILogDbExporter
{
    private readonly ILogger<LogDbExporterService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LogDbExporterOptions _options;
    private readonly DeliveryActivityTracker _activity;
    private readonly DeliveryConsoleBuffer _delivery;
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _rediscoverGate = new(1, 1);

    private const int MaxSentMessageLength = 512;

    private ILogDBClient _client;
    private string _currentEndpoint;

    private long _batchesSent;
    private long _recordsSent;
    private long _metricsBatchesSent;
    private long _metricsRecordsSent;
    private long _sendErrors;
    private long _retryCount;
    private DateTime? _lastSendUtc;
    private string? _lastError;
    private bool _healthy = true;
    private bool _enabledOverride;
    private bool _hasOverride;
    private DateTime _lastErrorLogUtc;

    public LogDbExporterService(ILogger<LogDbExporterService> logger, IOptions<LogDbExporterOptions> options,
        IOptions<CheckpointOptions> checkpointOptions, ILoggerFactory loggerFactory, DeliveryActivityTracker activity,
        DeliveryConsoleBuffer delivery)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _options = options.Value;
        _activity = activity;
        _delivery = delivery;

        // Persist toggle state next to checkpoints (on the Docker volume)
        var checkpointDir = Path.GetDirectoryName(Path.GetFullPath(checkpointOptions.Value.FilePath)) ?? ".";
        _settingsPath = Path.Combine(checkpointDir, "exporter-settings.json");
        LoadSettings();

        _currentEndpoint = _options.Endpoint;
        _client = BuildClient(_currentEndpoint);

        if (EndpointDiscovery.IsPlaceholder(_currentEndpoint))
        {
            _logger.LogWarning(
                "LogDB exporter endpoint is '{Endpoint}' — looks like a placeholder. Discovery will be retried when the exporter is enabled. Set LOGDB_EXPORTER_ENDPOINT to skip discovery.",
                _currentEndpoint);
        }
        else
        {
            _logger.LogInformation(
                "LogDB exporter initialized: endpoint={Endpoint}, protocol={Protocol}, enabled={Enabled}",
                _currentEndpoint, _options.Protocol, IsEnabled);
        }
    }

    private ILogDBClient BuildClient(string endpoint)
    {
        var protocol = Enum.TryParse<LogDBProtocol>(_options.Protocol, ignoreCase: true, out var parsedProtocol)
            ? parsedProtocol
            : LogDBProtocol.Native;

        var clientOptions = new LogDBLoggerOptions
        {
            ApiKey = _options.ApiKey,
            ServiceUrl = endpoint,
            Protocol = protocol,
            EnableBatching = true,
            BatchSize = _options.MaxBatchRecords,
            FlushInterval = TimeSpan.FromSeconds(Math.Max(1, _options.FlushIntervalSeconds)),
            EnableCompression = _options.EnableCompression,
            MaxRetries = Math.Max(0, _options.MaxRetries),
            EnableCircuitBreaker = true
        };

        return new LogDBClient(Options.Create(clientOptions), _loggerFactory.CreateLogger<LogDBClient>());
    }

    private async Task RediscoverEndpointIfNeededAsync()
    {
        if (!EndpointDiscovery.IsPlaceholder(_currentEndpoint)) return;

        if (!await _rediscoverGate.WaitAsync(0)) return;
        try
        {
            if (!EndpointDiscovery.IsPlaceholder(_currentEndpoint)) return;

            var resolved = await EndpointDiscovery.DiscoverGrpcLoggerUrlAsync(
                _options.ApiKey,
                msg => _logger.LogInformation("{Msg}", msg));

            if (string.IsNullOrWhiteSpace(resolved) || EndpointDiscovery.IsPlaceholder(resolved))
            {
                _logger.LogWarning("Discovery did not return a usable endpoint; exporter will keep dialing '{Endpoint}'.", _currentEndpoint);
                return;
            }

            var oldClient = _client;
            _client = BuildClient(resolved);
            _currentEndpoint = resolved;
            _logger.LogInformation("LogDB exporter endpoint updated via discovery: {Endpoint}", resolved);

            try { await oldClient.DisposeAsync(); } catch { /* old client already losing references */ }
        }
        finally
        {
            _rediscoverGate.Release();
        }
    }

    private bool IsEnabled => _hasOverride ? _enabledOverride : _options.Enabled;

    public void SetEnabled(bool enabled)
    {
        _enabledOverride = enabled;
        _hasOverride = true;
        SaveSettings();
        _logger.LogInformation("Exporter {State} via UI", enabled ? "enabled" : "disabled");

        if (enabled && EndpointDiscovery.IsPlaceholder(_currentEndpoint))
        {
            _ = Task.Run(async () =>
            {
                try { await RediscoverEndpointIfNeededAsync(); }
                catch (Exception ex) { _logger.LogWarning("Endpoint rediscovery failed: {Msg}", ex.Message); }
            });
        }
    }

    public ExporterStatus GetStatus()
    {
        return new ExporterStatus
        {
            Enabled = IsEnabled,
            Endpoint = _currentEndpoint,
            Healthy = _healthy,
            BatchesSent = Interlocked.Read(ref _batchesSent),
            RecordsSent = Interlocked.Read(ref _recordsSent),
            MetricsBatchesSent = Interlocked.Read(ref _metricsBatchesSent),
            MetricsRecordsSent = Interlocked.Read(ref _metricsRecordsSent),
            SendErrors = Interlocked.Read(ref _sendErrors),
            RetryCount = Interlocked.Read(ref _retryCount),
            LastSendUtc = _lastSendUtc,
            LastError = _lastError
        };
    }

    public async Task<bool> SendBatchAsync(List<LogRecord> batch, CancellationToken ct = default)
    {
        if (!IsEnabled || batch.Count == 0) return false;

        var startUtc = DateTime.UtcNow;

        // Built before the try so the catch block can mark the same records as failed.
        var prepared = new List<(LogRecord Record, int Count, Log Log)>();
        try
        {
            foreach (var a in AggregateBatch(batch))
                prepared.Add((a.Record, a.Count, MapToLogDbEvent(a.Record, a.Count)));

            var failCount = 0;
            foreach (var item in prepared)
            {
                var result = await _client.LogAsync(item.Log, ct);
                if (result != LogResponseStatus.Success)
                {
                    Interlocked.Increment(ref _retryCount);
                    failCount++;
                }
            }

            if (failCount > 0)
                _logger.LogWarning("gRPC batch: {FailCount}/{Total} returned non-Success", failCount, prepared.Count);

            await _client.FlushAsync();

            Interlocked.Increment(ref _batchesSent);
            Interlocked.Add(ref _recordsSent, batch.Count);
            _lastSendUtc = DateTime.UtcNow;
            _lastError = null;
            var wasUnhealthy = !_healthy;
            _healthy = true;
            if (wasUnhealthy)
                _logger.LogInformation("LogDB exporter recovered: {Endpoint} reachable again", _currentEndpoint);

            _activity.Record(startUtc, batch.Count, "delivered", metrics: false);
            var status = failCount > 0 ? $"ok ({failCount} retried)" : "ok";
            foreach (var item in prepared)
                _delivery.Record(BuildLogSentEntry(item.Record, item.Count, item.Log.Guid ?? "", "delivered", startUtc, status, error: null));
            _logger.LogDebug("Exported batch: {Raw} records -> {Aggregated} events", batch.Count, prepared.Count);
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _sendErrors);
            _lastError = ex.Message;
            _healthy = false;
            _activity.Record(startUtc, batch.Count, "failed", metrics: false);

            // If mapping failed before any record was prepared, fall back to the raw
            // batch so the operator still sees what was lost.
            var failedRecords = prepared.Count > 0
                ? prepared.Select(p => (p.Record, p.Count, Guid: p.Log.Guid ?? ""))
                : batch.Select(r => (Record: r, Count: 1, Guid: ""));
            foreach (var (record, count, guid) in failedRecords)
                _delivery.Record(BuildLogSentEntry(record, count, guid, "failed", startUtc, ex.GetType().Name, ex.Message));

            if ((DateTime.UtcNow - _lastErrorLogUtc).TotalSeconds >= 30)
            {
                _logger.LogError(
                    "Export failed ({Count} records) to {Endpoint} [{Protocol}]: {Msg} (errors={SendErrors}, retries={RetryCount})",
                    batch.Count, _currentEndpoint, _options.Protocol, ex.Message,
                    Interlocked.Read(ref _sendErrors), Interlocked.Read(ref _retryCount));
                _lastErrorLogUtc = DateTime.UtcNow;
            }
            return false;
        }
    }

    private static List<AggregatedRecord> AggregateBatch(List<LogRecord> batch)
    {
        var result = new List<AggregatedRecord>();

        // Group by (second, containerName, stream, message, parsedLevel)
        var groups = batch.GroupBy(r => (
            Second: new DateTime(r.Timestamp.Year, r.Timestamp.Month, r.Timestamp.Day,
                r.Timestamp.Hour, r.Timestamp.Minute, r.Timestamp.Second, r.Timestamp.Kind),
            r.ContainerName,
            r.Stream,
            r.Message,
            r.ParsedLevel
        ));

        foreach (var group in groups)
        {
            result.Add(new AggregatedRecord { Record = group.First(), Count = group.Count() });
        }

        return result;
    }

    private class AggregatedRecord
    {
        public LogRecord Record { get; set; } = null!;
        public int Count { get; set; }
    }

    private SentRecordEntry BuildLogSentEntry(LogRecord r, int count, string guid, string outcome,
        DateTime sentUtc, string? status, string? error)
    {
        var message = r.Message;
        if (message.Length > MaxSentMessageLength)
            message = message[..MaxSentMessageLength] + "...";

        return new SentRecordEntry
        {
            SentUtc = sentUtc,
            RecordTimestamp = r.Timestamp,
            Outcome = outcome,
            Kind = "log",
            AggregatedCount = count,
            ContainerId = r.ContainerId,
            Container = r.ContainerName,
            Image = r.Image,
            ComposeProject = r.ComposeProject,
            ComposeService = r.ComposeService,
            Stream = r.Stream,
            Level = r.ParsedLevel ?? (r.Stream == "stderr" ? "Error" : "Info"),
            Category = r.Category,
            Message = message,
            Guid = guid,
            Endpoint = _currentEndpoint,
            Status = status,
            Error = error
        };
    }

    private SentRecordEntry BuildMetricSentEntry(DockerMetricsRecord m, string guid, string outcome,
        DateTime sentUtc, string? status, string? error)
    {
        return new SentRecordEntry
        {
            SentUtc = sentUtc,
            RecordTimestamp = m.Timestamp,
            Outcome = outcome,
            Kind = "metric",
            AggregatedCount = 1,
            ContainerId = m.ContainerId,
            Container = m.ContainerName,
            Image = m.Image,
            ComposeProject = m.ComposeProject,
            ComposeService = m.ComposeService,
            Level = m.HealthStatus,
            Message = $"cpu {m.CpuUsagePercent:F1}% · mem {m.MemoryUsagePercent:F1}% ({FormatBytesShort(m.MemoryUsageBytes)})" +
                      $" · net rx {FormatBytesShort(m.NetworkRxBytes)} tx {FormatBytesShort(m.NetworkTxBytes)}",
            CpuPercent = m.CpuUsagePercent,
            MemoryPercent = m.MemoryUsagePercent,
            Guid = guid,
            Endpoint = _currentEndpoint,
            Status = status,
            Error = error
        };
    }

    private static string FormatBytesShort(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1_048_576) return $"{bytes / 1024.0:0.#}KB";
        if (bytes < 1_073_741_824) return $"{bytes / 1_048_576.0:0.#}MB";
        return $"{bytes / 1_073_741_824.0:0.#}GB";
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
            _logger.LogDebug("Exporter settings loaded: enabled={Enabled}", state.Enabled);
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
            var json = JsonSerializer.Serialize(new ExporterSettings { Enabled = _enabledOverride },
                new JsonSerializerOptions { WriteIndented = true });
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
    }

    public async Task<bool> SendMetricsBatchAsync(List<DockerMetricsRecord> batch, CancellationToken ct = default)
    {
        if (!IsEnabled || batch.Count == 0) return false;

        var startUtc = DateTime.UtcNow;

        // Built before the try so the catch block can mark the same records as failed.
        var prepared = new List<(DockerMetricsRecord Record, Log Log)>();
        try
        {
            foreach (var m in batch)
                prepared.Add((m, MapMetricToLogDbEvent(m)));

            var failCount = 0;
            foreach (var item in prepared)
            {
                var result = await _client.LogAsync(item.Log, ct);
                if (result != LogResponseStatus.Success)
                {
                    Interlocked.Increment(ref _retryCount);
                    failCount++;
                }
            }

            if (failCount > 0)
                _logger.LogWarning("gRPC metrics batch: {FailCount}/{Total} returned non-Success", failCount, prepared.Count);

            await _client.FlushAsync();

            // Metrics use dedicated counters so the log-facing "Records Sent" stat
            // stays comparable to what the live console shows (logs only).
            Interlocked.Increment(ref _metricsBatchesSent);
            Interlocked.Add(ref _metricsRecordsSent, batch.Count);
            _lastSendUtc = DateTime.UtcNow;
            _lastError = null;
            var wasUnhealthy = !_healthy;
            _healthy = true;
            if (wasUnhealthy)
                _logger.LogInformation("LogDB exporter recovered (metrics): {Endpoint} reachable again", _currentEndpoint);

            _activity.Record(startUtc, batch.Count, "delivered", metrics: true);
            var status = failCount > 0 ? $"ok ({failCount} retried)" : "ok";
            foreach (var item in prepared)
                _delivery.Record(BuildMetricSentEntry(item.Record, item.Log.Guid ?? "", "delivered", startUtc, status, error: null));
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _sendErrors);
            _lastError = ex.Message;
            _healthy = false;
            _activity.Record(startUtc, batch.Count, "failed", metrics: true);

            var failedRecords = prepared.Count > 0
                ? prepared.Select(p => (p.Record, Guid: p.Log.Guid ?? ""))
                : batch.Select(m => (Record: m, Guid: ""));
            foreach (var (record, guid) in failedRecords)
                _delivery.Record(BuildMetricSentEntry(record, guid, "failed", startUtc, ex.GetType().Name, ex.Message));

            if ((DateTime.UtcNow - _lastErrorLogUtc).TotalSeconds >= 30)
            {
                _logger.LogError(
                    "Metrics export failed ({Count} records) to {Endpoint} [{Protocol}]: {Msg} (errors={SendErrors}, retries={RetryCount})",
                    batch.Count, _currentEndpoint, _options.Protocol, ex.Message,
                    Interlocked.Read(ref _sendErrors), Interlocked.Read(ref _retryCount));
                _lastErrorLogUtc = DateTime.UtcNow;
            }
            return false;
        }
    }

    private Log MapMetricToLogDbEvent(DockerMetricsRecord m)
    {
        var metric = new LogDockerMetric
        {
            Timestamp = m.Timestamp,
            Collection = !string.IsNullOrEmpty(m.ComposeService) ? m.ComposeService : m.ContainerName,
            ContainerId = m.ContainerId,
            ContainerName = m.ContainerName,
            Image = m.Image,
            ImageTag = m.ImageTag,
            HostName = m.HostName,
            ContainerState = m.ContainerState,
            ContainerStatus = m.ContainerStatus,
            ComposeProject = m.ComposeProject,
            ComposeService = m.ComposeService,
            HealthStatus = m.HealthStatus,
            CpuUsagePercent = m.CpuUsagePercent,
            CpuTotalUsage = (long)m.CpuTotalUsage,
            CpuSystemUsage = (long)m.CpuSystemUsage,
            CpuOnlineCpus = (int)m.CpuOnlineCpus,
            MemoryUsageBytes = (long)m.MemoryUsageBytes,
            MemoryLimitBytes = (long)m.MemoryLimitBytes,
            MemoryUsagePercent = m.MemoryUsagePercent,
            MemoryMaxUsageBytes = (long)m.MemoryMaxUsageBytes,
            NetworkRxBytes = (long)m.NetworkRxBytes,
            NetworkTxBytes = (long)m.NetworkTxBytes,
            NetworkRxPackets = (long)m.NetworkRxPackets,
            NetworkTxPackets = (long)m.NetworkTxPackets,
            BlockIoReadBytes = (long)m.BlockIoReadBytes,
            BlockIoWriteBytes = (long)m.BlockIoWriteBytes,
            PidsCurrent = (int)m.PidsCurrent,
            RestartCount = (int)m.RestartCount,
            Labels = m.Labels.ToDictionary(kv => kv.Key, kv => kv.Value)
        };

        return metric.ToLog();
    }

    private Log MapToLogDbEvent(LogRecord r, int count = 1)
    {
        // Use parsed level from .NET ConsoleLogger if available, otherwise infer from stream
        var level = r.Stream == "stderr" ? "Error" : "Info";
        if (r.ParsedLevel is not null)
            level = r.ParsedLevel;

        var message = count > 1 ? $"[x{count}] {r.Message}" : r.Message;

        var evt = new LogDockerEvent
        {
            Timestamp = r.Timestamp,
            Collection = !string.IsNullOrEmpty(r.ComposeService) ? r.ComposeService : r.ContainerName,
            ContainerId = r.ContainerId,
            ContainerName = r.ContainerName,
            Image = r.Image,
            Stream = r.Stream,
            Level = level,
            Message = message,
            HostName = r.HostName,
            Source = $"{r.HostName}/{r.ContainerName}",
            ComposeProject = r.ComposeProject,
            ComposeService = r.ComposeService,
            Labels = r.Labels.ToDictionary(kv => kv.Key, kv => kv.Value)
        };

        var log = evt.ToLog();

        // Preserve extra attributes not on the typed model
        if (r.Category is not null) log.AttributesS["dotnet.logger.category"] = r.Category;
        log.AttributesS["source.type"] = r.SourceType;
        if (count > 1) log.AttributesN["count"] = count;
        if (r.Stream == "stderr") log.Label.Add("stderr");

        return log;
    }
}
