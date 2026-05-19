using System.Diagnostics;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using LogDB.Client.Models;
using LogDB.Extensions.Logging;

namespace com.logdb.windows.collector.Services;

/// <summary>
/// Periodically emits a LogBeat record (time-series heartbeat) via the
/// SDK's LogBeatAsync, populated from user-selected built-in fields plus
/// user-defined custom tags. Each beat carries a single measurement (default
/// "heartbeat") in a collection (default "beats") and lives in the
/// LogDB log_beats table on the server side.
/// </summary>
public sealed class HeartbeatBeatExportService : BackgroundService
{
    private readonly ILogger<HeartbeatBeatExportService> _logger;
    private readonly ILogDBClient _logDBClient;
    private readonly IConfiguration _configuration;

    private readonly int _intervalSeconds;
    private readonly string _measurement;
    private readonly string _collection;
    private readonly string _application;
    private readonly string _environment;
    private readonly string _serverName;

    private readonly bool _includeUptime;
    private readonly bool _includeHostnameTag;
    private readonly bool _includeAppVersionTag;
    private readonly bool _includeCpuPercent;
    private readonly bool _includeMemoryPercent;

    private readonly List<(string Key, string Value)> _customTags;

    // CPU performance counter — lazily initialised, primed on first real call.
    private readonly Lazy<PerformanceCounter?> _cpuCounter;
    private bool _cpuCounterPrimed;

    public HeartbeatBeatExportService(
        ILogger<HeartbeatBeatExportService> logger,
        IConfiguration configuration,
        ILogDBClient logDBClient)
    {
        _logger = logger;
        _logDBClient = logDBClient;
        _configuration = configuration;

        _intervalSeconds = Math.Max(5, configuration.GetValue<int>("Heartbeat:IntervalSeconds", 60));
        _measurement = configuration["Heartbeat:Measurement"] ?? "heartbeat";
        _collection = configuration["Heartbeat:Collection"] ?? "beats";

        _application = "LogDB Collector";
        _environment = configuration["Server:ServerEnvironment"] ?? "Production";
        _serverName = configuration["Server:ServerName"] ?? Environment.MachineName;

        _includeUptime = configuration.GetValue<bool>("Heartbeat:IncludeUptime", true);
        _includeHostnameTag = configuration.GetValue<bool>("Heartbeat:IncludeHostnameTag", true);
        _includeAppVersionTag = configuration.GetValue<bool>("Heartbeat:IncludeAppVersionTag", false);
        _includeCpuPercent = configuration.GetValue<bool>("Heartbeat:IncludeCpuPercent", false);
        _includeMemoryPercent = configuration.GetValue<bool>("Heartbeat:IncludeMemoryPercent", false);

        _customTags = ReadCustomTags(configuration);

        _cpuCounter = new Lazy<PerformanceCounter?>(() =>
        {
            if (!_includeCpuPercent) return null;
            try
            {
                var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                counter.NextValue(); // first read always 0, prime it
                return counter;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Heartbeat: failed to init CPU PerformanceCounter; CPU% will be omitted from beats.");
                return null;
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Show every identity field the beat will carry so an operator can
        // verify at one glance which overrides survived the save/reload path.
        // (Environment was previously logged via the wrong key name in the
        // module wrapper — see HeartbeatCollectorModule for context.)
        _logger.LogInformation(
            "Heartbeat service starting (interval={Interval}s measurement={Measurement} collection={Collection} environment={Environment} serverName={ServerName} application={Application})",
            _intervalSeconds, _measurement, _collection, _environment, _serverName, _application);

        // Initial delay so the SDK has a chance to settle before the first beat.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EmitBeatAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Heartbeat tick failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("Heartbeat service stopped");
    }

    private async Task EmitBeatAsync(CancellationToken cancellationToken)
    {
        var beat = new LogBeat
        {
            Guid = Guid.NewGuid().ToString(),
            Timestamp = DateTime.UtcNow,
            Measurement = _measurement,
            Collection = _collection,
            Application = _application,
            Environment = _environment,
        };

        // Tags
        if (_includeHostnameTag)
        {
            beat.Tag.Add(new LogMeta { Key = "host", Value = _serverName });
        }

        if (_includeAppVersionTag)
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                          ?? "unknown";
            beat.Tag.Add(new LogMeta { Key = "app_version", Value = version });
        }

        foreach (var (k, v) in _customTags)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            beat.Tag.Add(new LogMeta { Key = k, Value = v ?? string.Empty });
        }

        // Fields (numeric/string values shipped per beat)
        if (_includeUptime)
        {
            var uptimeSeconds = Environment.TickCount64 / 1000L;
            beat.Field.Add(new LogMeta { Key = "uptime_seconds", Value = uptimeSeconds.ToString() });
        }

        if (_includeCpuPercent)
        {
            var cpu = ReadCpuPercent();
            if (cpu.HasValue)
            {
                beat.Field.Add(new LogMeta { Key = "cpu_percent", Value = Math.Round(cpu.Value, 2).ToString("0.##") });
            }
        }

        if (_includeMemoryPercent)
        {
            var mem = ReadMemoryPercent();
            if (mem.HasValue)
            {
                beat.Field.Add(new LogMeta { Key = "memory_percent", Value = Math.Round(mem.Value, 2).ToString("0.##") });
            }
        }

        var result = await _logDBClient.LogBeatAsync(beat, cancellationToken);

        var summary = string.Join(", ", beat.Field.Select(f => $"{f.Key}={f.Value}"));
        if (result == LogResponseStatus.Success)
        {
            _logger.LogInformation("► [Heartbeat] {Measurement}/{Collection} → {Fields}",
                _measurement, _collection, summary);
        }
        else
        {
            _logger.LogWarning("Heartbeat send returned {Status} ({Measurement}/{Collection})",
                result, _measurement, _collection);
        }
    }

    private double? ReadCpuPercent()
    {
        try
        {
            var counter = _cpuCounter.Value;
            if (counter == null) return null;

            if (!_cpuCounterPrimed)
            {
                Thread.Sleep(500);
                _cpuCounterPrimed = true;
            }
            return counter.NextValue();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ReadCpuPercent failed (non-fatal)");
            return null;
        }
    }

    private double? ReadMemoryPercent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");

            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    var total = obj["TotalVisibleMemorySize"] is { } t ? Convert.ToDouble(t) : 0d;
                    var free = obj["FreePhysicalMemory"] is { } f ? Convert.ToDouble(f) : 0d;
                    if (total <= 0) return null;
                    return ((total - free) / total) * 100.0;
                }
            }
        }
        catch (ManagementException ex)
        {
            _logger.LogDebug(ex, "ReadMemoryPercent: WMI failed (non-fatal)");
        }
        catch (COMException ex)
        {
            _logger.LogDebug(ex, "ReadMemoryPercent: COM failed (non-fatal)");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "ReadMemoryPercent: access denied (non-fatal)");
        }
        return null;
    }

    private static List<(string Key, string Value)> ReadCustomTags(IConfiguration configuration)
    {
        var tags = new List<(string, string)>();
        var section = configuration.GetSection("Heartbeat:Tags");
        if (!section.Exists()) return tags;

        // Indexed pairs: Heartbeat:Tags:0:Key / :Value, Heartbeat:Tags:1:Key / :Value, …
        foreach (var child in section.GetChildren())
        {
            var k = child["Key"];
            var v = child["Value"];
            if (!string.IsNullOrWhiteSpace(k))
            {
                tags.Add((k, v ?? string.Empty));
            }
        }
        return tags;
    }

    public override void Dispose()
    {
        if (_cpuCounter.IsValueCreated)
        {
            _cpuCounter.Value?.Dispose();
        }
        base.Dispose();
    }
}
