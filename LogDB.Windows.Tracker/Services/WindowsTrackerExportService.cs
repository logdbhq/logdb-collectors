using com.logdb.windows.tracker.Models;
using LogDB.Client.Models;
using LogDB.Extensions.Logging;
using Microsoft.Extensions.Logging;
using LogLevel = LogDB.Client.Models.LogLevel;

namespace com.logdb.windows.tracker.Services;

/// <summary>
/// Background service that periodically collects Windows system metrics
/// and exports them to LogDB via gRPC.
/// </summary>
public class WindowsTrackerExportService : BackgroundService
{
    private readonly ILogger<WindowsTrackerExportService> _logger;
    private readonly ILogDBClient _logDBClient;
    private readonly WindowsMetricsReader _metricsReader;
    private readonly int _collectionIntervalSeconds;
    private readonly string _collection;
    private readonly string _serverName;
    private readonly string _serverEnvironment;
    private readonly List<string> _defaultLabels;

    // Retry configuration
    private const int MaxRetries = 3;
    private const int MaxQueueSize = 1000;
    private readonly Queue<Log> _failedMetricsQueue = new();

    // Self-diagnostics configuration
    private readonly bool _enableSelfLogging;

    public WindowsTrackerExportService(
        ILogger<WindowsTrackerExportService> logger,
        IConfiguration configuration,
        ILogDBClient logDBClient,
        WindowsMetricsReader metricsReader)
    {
        _logger = logger;
        _logDBClient = logDBClient;
        _metricsReader = metricsReader;
        _collectionIntervalSeconds = configuration.GetValue<int>("WindowsTracker:CollectionIntervalSeconds", 60);
        _collection = configuration["WindowsTracker:Collection"] ?? "windows-metrics";
        _serverName = configuration["Server:ServerName"] ?? Environment.MachineName;
        _serverEnvironment = configuration["Server:ServerEnvironment"] ?? "Production";
        _enableSelfLogging = configuration.GetValue<bool>("WindowsTracker:EnableSelfLogging", true);
        _defaultLabels = configuration.GetSection("Server:DefaultLabels").Get<List<string>>()
            ?? new List<string> { "windows-tracker", "metrics" };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Windows Tracker Export Service starting...");
        _logger.LogInformation("Collection interval: {Interval} seconds, target: {Collection}",
            _collectionIntervalSeconds, _collection);

        await LogToUserCollectionAsync(
            LogLevel.Info,
            $"Windows Tracker service started on {_serverName} (interval: {_collectionIntervalSeconds}s)",
            cancellationToken: stoppingToken);

        // Initial delay to let the system stabilize
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RetryFailedMetricsAsync(stoppingToken);
                await CollectAndExportMetricsAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in metrics collection cycle");
                await LogToUserCollectionAsync(
                    LogLevel.Error,
                    $"Metrics collection cycle failed: {ex.Message}",
                    ex,
                    stoppingToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(_collectionIntervalSeconds), stoppingToken);
        }

        _logger.LogInformation("Windows Tracker Export Service stopped");

        try
        {
            await LogToUserCollectionAsync(
                LogLevel.Info,
                $"Windows Tracker service stopped on {_serverName}");
        }
        catch { /* ignore */ }
    }

    private async Task CollectAndExportMetricsAsync(CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogDebug("Starting metrics collection...");

        var metrics = await _metricsReader.CollectAllMetricsAsync(cancellationToken);

        if (metrics.Count == 0)
        {
            _logger.LogWarning("No metrics collected");
            return;
        }

        var successCount = 0;
        var failCount = 0;

        foreach (var metric in metrics)
        {
            var log = ConvertToLogDto(metric);
            var success = await ExportWithRetryAsync(log, metric.Measurement, cancellationToken);

            if (success)
            {
                successCount++;
                var fieldsSummary = string.Join(", ", metric.Fields.Select(f => $"{f.Key}={f.Value}"));
                Console.WriteLine($"  > {metric.Measurement}: {fieldsSummary}");

                _logger.LogInformation("► [Metrics] {Measurement}: {Fields}",
                    metric.Measurement, fieldsSummary);
            }
            else
            {
                failCount++;
                QueueFailedMetric(log);
            }
        }

        var elapsed = DateTime.UtcNow - startTime;
        _logger.LogInformation(
            "Metrics cycle completed: {Success}/{Total} sent ({Elapsed:F0}ms)",
            successCount, metrics.Count, elapsed.TotalMilliseconds);
    }

    private Log ConvertToLogDto(SystemMetric metric)
    {
        var labels = new List<string>(_defaultLabels)
        {
            metric.Measurement,
            _serverName.ToLower()
        };

        var message = BuildMessage(metric);

        // Build a typed LogWindowsMetric and let ToLog() handle _sys_type and attribute mapping
        var wm = new LogWindowsMetric
        {
            Guid = Guid.NewGuid().ToString(),
            Timestamp = metric.Time,
            Measurement = metric.Measurement,
            ServerName = _serverName,
            Environment = _serverEnvironment,
            Collection = _collection
        };

        // Map typed fields based on measurement category
        // Track which tags/fields are consumed so we can carry over extras
        var consumedTags = new HashSet<string>();
        var consumedFields = new HashSet<string>();

        switch (metric.Measurement)
        {
            case "cpu":
                if (metric.Fields.TryGetValue("usage_percent", out var cpuUsage))
                    { wm.CpuUsagePercent = cpuUsage; consumedFields.Add("usage_percent"); }
                if (metric.Fields.TryGetValue("idle_percent", out var cpuIdle))
                    { wm.CpuIdlePercent = cpuIdle; consumedFields.Add("idle_percent"); }
                if (metric.Fields.TryGetValue("core_count", out var cores))
                    { wm.CpuCoreCount = (int)cores; consumedFields.Add("core_count"); }
                break;

            case "memory":
                if (metric.Fields.TryGetValue("total_gb", out var memTotal))
                    { wm.MemoryTotalGb = memTotal; consumedFields.Add("total_gb"); }
                if (metric.Fields.TryGetValue("used_gb", out var memUsed))
                    { wm.MemoryUsedGb = memUsed; consumedFields.Add("used_gb"); }
                if (metric.Fields.TryGetValue("free_gb", out var memFree))
                    { wm.MemoryFreeGb = memFree; consumedFields.Add("free_gb"); }
                if (metric.Fields.TryGetValue("usage_percent", out var memPct))
                    { wm.MemoryUsagePercent = memPct; consumedFields.Add("usage_percent"); }
                break;

            case "disk":
                if (metric.Tags.TryGetValue("drive_letter", out var dl))
                    { wm.DriveLetter = dl; consumedTags.Add("drive_letter"); }
                if (metric.Tags.TryGetValue("drive_type", out var dt))
                    { wm.DriveType = dt; consumedTags.Add("drive_type"); }
                if (metric.Tags.TryGetValue("file_system", out var fs))
                    { wm.FileSystem = fs; consumedTags.Add("file_system"); }
                if (metric.Fields.TryGetValue("total_gb", out var diskTotal))
                    { wm.DiskTotalGb = diskTotal; consumedFields.Add("total_gb"); }
                if (metric.Fields.TryGetValue("used_gb", out var diskUsed))
                    { wm.DiskUsedGb = diskUsed; consumedFields.Add("used_gb"); }
                if (metric.Fields.TryGetValue("free_gb", out var diskFree))
                    { wm.DiskFreeGb = diskFree; consumedFields.Add("free_gb"); }
                if (metric.Fields.TryGetValue("usage_percent", out var diskPct))
                    { wm.DiskUsagePercent = diskPct; consumedFields.Add("usage_percent"); }
                break;

            case "network":
                if (metric.Tags.TryGetValue("interface_name", out var ifName))
                    { wm.InterfaceName = ifName; consumedTags.Add("interface_name"); }
                if (metric.Tags.TryGetValue("interface_type", out var ifType))
                    { wm.InterfaceType = ifType; consumedTags.Add("interface_type"); }
                if (metric.Fields.TryGetValue("send_bytes_per_sec", out var sent))
                    { wm.NetworkBytesSent = sent; consumedFields.Add("send_bytes_per_sec"); }
                if (metric.Fields.TryGetValue("recv_bytes_per_sec", out var recv))
                    { wm.NetworkBytesReceived = recv; consumedFields.Add("recv_bytes_per_sec"); }
                if (metric.Fields.TryGetValue("speed_mbps", out var speed))
                    { wm.NetworkSpeedMbps = speed; consumedFields.Add("speed_mbps"); }
                break;
        }

        var log = wm.ToLog();

        // Override message and source with our richer versions
        log.Message = message;
        log.Source = $"windows-tracker/{metric.Measurement}";
        log.Application = "Windows Tracker";
        foreach (var lbl in labels)
            if (!log.Label.Contains(lbl)) log.Label.Add(lbl);

        // Carry over any extra tags not consumed by the typed model
        foreach (var tag in metric.Tags)
        {
            if (!consumedTags.Contains(tag.Key) && !log.AttributesS.ContainsKey(tag.Key))
                log.AttributesS[tag.Key] = tag.Value;
        }

        // Carry over any extra fields not consumed by the typed model
        foreach (var field in metric.Fields)
        {
            if (!consumedFields.Contains(field.Key) && !log.AttributesN.ContainsKey(field.Key))
                log.AttributesN[field.Key] = field.Value;
        }

        return log;
    }

    private string BuildMessage(SystemMetric metric)
    {
        return metric.Measurement switch
        {
            "cpu" => $"CPU: {metric.Fields.GetValueOrDefault("usage_percent", 0):F1}% usage",
            "memory" => $"Memory: {metric.Fields.GetValueOrDefault("usage_percent", 0):F1}% " +
                       $"({metric.Fields.GetValueOrDefault("used_gb", 0):F1} GB / " +
                       $"{metric.Fields.GetValueOrDefault("total_gb", 0):F1} GB)",
            "disk" => $"Disk {metric.Tags.GetValueOrDefault("drive_letter", "?")}: " +
                     $"{metric.Fields.GetValueOrDefault("usage_percent", 0):F1}% " +
                     $"({metric.Fields.GetValueOrDefault("free_gb", 0):F1} GB free)",
            "network" => $"Network {metric.Tags.GetValueOrDefault("interface_name", "?")}: " +
                        $"{FormatRate(metric.Fields.GetValueOrDefault("send_bytes_per_sec", 0))} up, " +
                        $"{FormatRate(metric.Fields.GetValueOrDefault("recv_bytes_per_sec", 0))} down",
            _ => $"{metric.Measurement}: {string.Join(", ", metric.Fields.Select(f => $"{f.Key}={f.Value}"))}"
        };
    }

    private string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec >= 1_073_741_824) return $"{bytesPerSec / 1_073_741_824:F1} GB/s";
        if (bytesPerSec >= 1_048_576) return $"{bytesPerSec / 1_048_576:F1} MB/s";
        if (bytesPerSec >= 1024) return $"{bytesPerSec / 1024:F1} KB/s";
        return $"{bytesPerSec:F0} B/s";
    }

    private async Task<bool> ExportWithRetryAsync(Log log, string measurementName, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var result = await _logDBClient.LogAsync(log, cancellationToken);

                if (result == LogResponseStatus.Success)
                {
                    return true;
                }

                _logger.LogWarning("Failed to send {Measurement} metric (attempt {Attempt}/{Max}): {Status}",
                    measurementName, attempt, MaxRetries, result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                _logger.LogWarning(ex, "Error sending {Measurement} metric (attempt {Attempt}/{Max}), retrying in {Delay}s",
                    measurementName, attempt, MaxRetries, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send {Measurement} metric after {Max} attempts",
                    measurementName, MaxRetries);

                _ = LogToUserCollectionAsync(
                    LogLevel.Warning,
                    $"Failed to export {measurementName} metric after {MaxRetries} attempts: {ex.Message}",
                    ex,
                    cancellationToken);
            }
        }

        return false;
    }

    private void QueueFailedMetric(Log log)
    {
        if (_failedMetricsQueue.Count >= MaxQueueSize)
        {
            _failedMetricsQueue.Dequeue();
            _logger.LogWarning("Failed metrics queue full ({Max}), dropping oldest metric", MaxQueueSize);
        }

        _failedMetricsQueue.Enqueue(log);
        _logger.LogDebug("Queued failed metric for retry (queue size: {Size})", _failedMetricsQueue.Count);
    }

    private async Task LogToUserCollectionAsync(
        LogLevel level,
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        if (!_enableSelfLogging) return;

        try
        {
            var log = new Log
            {
                Guid = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow,
                Application = "Windows Tracker",
                Environment = _serverEnvironment,
                Level = level,
                Message = message,
                Source = "windows-tracker/diagnostics",
                Collection = _collection,
                Label = new List<string> { "windows-tracker", "diagnostics", _serverName.ToLower() },
                AttributesS = new Dictionary<string, string>
                {
                    ["serverName"] = _serverName,
                    ["environment"] = _serverEnvironment,
                    ["_sys_type"] = "tracker_diagnostic"
                }
            };

            if (exception != null)
            {
                log.AttributesS["exceptionType"] = exception.GetType().Name;
                log.AttributesS["exceptionMessage"] = exception.Message;
                log.AttributesS["stackTrace"] = exception.StackTrace ?? "";
            }

            await _logDBClient.LogAsync(log, cancellationToken);
        }
        catch
        {
            // Silently ignore
        }
    }

    private async Task RetryFailedMetricsAsync(CancellationToken cancellationToken)
    {
        var retryCount = _failedMetricsQueue.Count;
        if (retryCount == 0) return;

        _logger.LogInformation("Retrying {Count} previously failed metrics...", retryCount);

        var successCount = 0;
        var stillFailed = new List<Log>();

        for (int i = 0; i < retryCount && !cancellationToken.IsCancellationRequested; i++)
        {
            var log = _failedMetricsQueue.Dequeue();
            var measurementName = log.AttributesS?.GetValueOrDefault("measurement", "unknown") ?? "unknown";

            try
            {
                var result = await _logDBClient.LogAsync(log, cancellationToken);

                if (result == LogResponseStatus.Success)
                    successCount++;
                else
                    stillFailed.Add(log);
            }
            catch (OperationCanceledException)
            {
                stillFailed.Add(log);
                while (_failedMetricsQueue.Count > 0)
                    stillFailed.Add(_failedMetricsQueue.Dequeue());
                break;
            }
            catch
            {
                stillFailed.Add(log);
            }
        }

        foreach (var log in stillFailed)
        {
            if (_failedMetricsQueue.Count < MaxQueueSize)
                _failedMetricsQueue.Enqueue(log);
        }

        if (successCount > 0 || stillFailed.Count > 0)
        {
            _logger.LogInformation("Retry complete: {Success} succeeded, {Failed} still pending",
                successCount, stillFailed.Count);
        }
    }
}
