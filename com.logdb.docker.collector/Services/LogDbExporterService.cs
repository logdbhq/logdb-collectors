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
    private readonly LogDbExporterOptions _options;
    private readonly ILogDBClient _client;
    private readonly string _settingsPath;

    private long _batchesSent;
    private long _recordsSent;
    private long _sendErrors;
    private long _retryCount;
    private DateTime? _lastSendUtc;
    private string? _lastError;
    private bool _healthy = true;
    private bool _enabledOverride;
    private bool _hasOverride;
    private DateTime _lastErrorLogUtc;

    public LogDbExporterService(ILogger<LogDbExporterService> logger, IOptions<LogDbExporterOptions> options,
        IOptions<CheckpointOptions> checkpointOptions, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _options = options.Value;

        // Persist toggle state next to checkpoints (on the Docker volume)
        var checkpointDir = Path.GetDirectoryName(Path.GetFullPath(checkpointOptions.Value.FilePath)) ?? ".";
        _settingsPath = Path.Combine(checkpointDir, "exporter-settings.json");
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

    public ExporterStatus GetStatus()
    {
        return new ExporterStatus
        {
            Enabled = IsEnabled,
            Endpoint = _options.Endpoint,
            Healthy = _healthy,
            BatchesSent = Interlocked.Read(ref _batchesSent),
            RecordsSent = Interlocked.Read(ref _recordsSent),
            SendErrors = Interlocked.Read(ref _sendErrors),
            RetryCount = Interlocked.Read(ref _retryCount),
            LastSendUtc = _lastSendUtc,
            LastError = _lastError
        };
    }

    public async Task<bool> SendBatchAsync(List<LogRecord> batch, CancellationToken ct = default)
    {
        if (!IsEnabled || batch.Count == 0) return false;

        try
        {
            var aggregated = AggregateBatch(batch);
            var logs = aggregated.Select(a => MapToLogDbEvent(a.Record, a.Count)).ToList();

            var failCount = 0;
            foreach (var log in logs)
            {
                var result = await _client.LogAsync(log, ct);
                if (result != LogResponseStatus.Success)
                {
                    Interlocked.Increment(ref _retryCount);
                    failCount++;
                }
            }

            if (failCount > 0)
                _logger.LogWarning("gRPC batch: {FailCount}/{Total} returned non-Success", failCount, logs.Count);

            await _client.FlushAsync();

            Interlocked.Increment(ref _batchesSent);
            Interlocked.Add(ref _recordsSent, batch.Count);
            _lastSendUtc = DateTime.UtcNow;
            _lastError = null;
            _healthy = true;

            _logger.LogDebug("Exported batch: {Raw} records -> {Aggregated} events", batch.Count, logs.Count);
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _sendErrors);
            _lastError = ex.Message;
            _healthy = false;

            if ((DateTime.UtcNow - _lastErrorLogUtc).TotalSeconds >= 30)
            {
                _logger.LogError("Export failed ({Count} records): {Msg}", batch.Count, ex.Message);
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

        try
        {
            var logs = batch.Select(MapMetricToLogDbEvent).ToList();

            var failCount = 0;
            foreach (var log in logs)
            {
                var result = await _client.LogAsync(log, ct);
                if (result != LogResponseStatus.Success)
                {
                    Interlocked.Increment(ref _retryCount);
                    failCount++;
                }
            }

            if (failCount > 0)
                _logger.LogWarning("gRPC metrics batch: {FailCount}/{Total} returned non-Success", failCount, logs.Count);

            await _client.FlushAsync();

            Interlocked.Increment(ref _batchesSent);
            Interlocked.Add(ref _recordsSent, batch.Count);
            _lastSendUtc = DateTime.UtcNow;
            _lastError = null;
            _healthy = true;
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _sendErrors);
            _lastError = ex.Message;
            _healthy = false;

            if ((DateTime.UtcNow - _lastErrorLogUtc).TotalSeconds >= 30)
            {
                _logger.LogError("Metrics export failed ({Count} records): {Msg}", batch.Count, ex.Message);
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
