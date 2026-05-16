using com.logdb.windows.tracker.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace com.logdb.windows.tracker.Services;

/// <summary>
/// Reads Windows system metrics including CPU, Memory, Disk (space + I/O), and Network (with deltas).
/// </summary>
public class WindowsMetricsReader
{
    private readonly ILogger<WindowsMetricsReader> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _serverName;
    private readonly string _serverEnvironment;

    // Thread-safe lazy initialization for CPU performance counter
    private readonly Lazy<PerformanceCounter?> _cpuCounter;
    private bool _cpuCounterPrimed;

    // Network delta tracking: interface name -> (bytesSent, bytesReceived, timestamp)
    private readonly Dictionary<string, (long bytesSent, long bytesReceived, DateTime time)> _previousNetworkStats = new();

    public WindowsMetricsReader(
        ILogger<WindowsMetricsReader> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _serverName = _configuration["Server:ServerName"] ?? Environment.MachineName;
        _serverEnvironment = _configuration["Server:ServerEnvironment"] ?? "Production";

        _cpuCounter = new Lazy<PerformanceCounter?>(() =>
        {
            try
            {
                var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                // First call always returns 0, so prime it
                counter.NextValue();
                return counter;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize CPU performance counter, will use WMI fallback");
                return null;
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Collects all enabled metrics based on configuration.
    /// </summary>
    public async Task<List<SystemMetric>> CollectAllMetricsAsync(CancellationToken cancellationToken = default)
    {
        var metrics = new List<SystemMetric>();

        var collectCpu = _configuration.GetValue<bool>("WindowsTracker:Metrics:CPU", true);
        var collectMemory = _configuration.GetValue<bool>("WindowsTracker:Metrics:Memory", true);
        var collectDisk = _configuration.GetValue<bool>("WindowsTracker:Metrics:Disk", true);
        var collectNetwork = _configuration.GetValue<bool>("WindowsTracker:Metrics:Network", false);

        try
        {
            if (collectCpu)
            {
                var cpuMetric = await CollectCpuMetricAsync(cancellationToken);
                if (cpuMetric != null)
                    metrics.Add(cpuMetric);
            }

            if (collectMemory)
            {
                var memoryMetric = CollectMemoryMetric();
                if (memoryMetric != null)
                    metrics.Add(memoryMetric);
            }

            if (collectDisk)
            {
                var diskMetrics = CollectDiskMetrics();
                metrics.AddRange(diskMetrics);
            }

            if (collectNetwork)
            {
                var networkMetrics = CollectNetworkMetrics();
                metrics.AddRange(networkMetrics);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        return metrics;
    }

    /// <summary>
    /// Collects CPU usage metrics.
    /// </summary>
    private async Task<SystemMetric?> CollectCpuMetricAsync(CancellationToken cancellationToken)
    {
        try
        {
            double cpuUsage = 0;
            var counter = _cpuCounter.Value;

            if (counter != null)
            {
                // PerformanceCounter needs two reads with a delay between them.
                // The Lazy constructor primes it once. On the very first CollectCpuMetricAsync call,
                // we wait briefly so the second read (below) returns a real value.
                if (!_cpuCounterPrimed)
                {
                    await Task.Delay(500, cancellationToken);
                    _cpuCounterPrimed = true;
                }
                cpuUsage = counter.NextValue();
            }
            else
            {
                cpuUsage = GetCpuUsageViaWmi();
            }

            return new SystemMetric
            {
                Measurement = "cpu",
                Time = DateTime.UtcNow,
                Tags = new Dictionary<string, string>
                {
                    ["server_name"] = _serverName,
                    ["environment"] = _serverEnvironment,
                    ["metric_type"] = "cpu"
                },
                Fields = new Dictionary<string, double>
                {
                    ["usage_percent"] = Math.Round(cpuUsage, 2),
                    ["idle_percent"] = Math.Round(100 - cpuUsage, 2),
                    ["core_count"] = Environment.ProcessorCount
                }
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied reading CPU performance counter. Ensure the service runs with sufficient permissions.");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "CPU performance counter not available on this system");
            return null;
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "Windows error reading CPU metrics (error code: {ErrorCode})", ex.NativeErrorCode);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private double GetCpuUsageViaWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor");
            double totalLoad = 0;
            int count = 0;

            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    var load = obj["LoadPercentage"];
                    if (load != null)
                    {
                        totalLoad += Convert.ToDouble(load);
                        count++;
                    }
                }
            }

            return count > 0 ? totalLoad / count : 0;
        }
        catch (ManagementException ex)
        {
            _logger.LogWarning(ex, "WMI CPU query failed (status: {Status})", ex.ErrorCode);
            return 0;
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "COM error in WMI CPU query (HRESULT: {HResult})", ex.HResult);
            return 0;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied for WMI CPU query");
            return 0;
        }
    }

    /// <summary>
    /// Collects memory usage metrics.
    /// </summary>
    private SystemMetric? CollectMemoryMetric()
    {
        try
        {
            double totalMemoryBytes = 0;
            double freeMemoryBytes = 0;

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
                foreach (ManagementObject obj in searcher.Get())
                {
                    using (obj)
                    {
                        var total = obj["TotalVisibleMemorySize"];
                        var free = obj["FreePhysicalMemory"];

                        if (total != null)
                            totalMemoryBytes = Convert.ToDouble(total) * 1024;
                        if (free != null)
                            freeMemoryBytes = Convert.ToDouble(free) * 1024;
                    }
                }
            }
            catch (ManagementException ex)
            {
                _logger.LogWarning(ex, "WMI memory query failed (status: {Status}), using GC info as fallback", ex.ErrorCode);
                var gcMemory = GC.GetGCMemoryInfo();
                totalMemoryBytes = gcMemory.TotalAvailableMemoryBytes;
                freeMemoryBytes = gcMemory.TotalAvailableMemoryBytes - GC.GetTotalMemory(false);
            }
            catch (COMException ex)
            {
                _logger.LogWarning(ex, "COM error in WMI memory query (HRESULT: {HResult}), using GC info as fallback", ex.HResult);
                var gcMemory = GC.GetGCMemoryInfo();
                totalMemoryBytes = gcMemory.TotalAvailableMemoryBytes;
                freeMemoryBytes = gcMemory.TotalAvailableMemoryBytes - GC.GetTotalMemory(false);
            }

            var usedMemoryBytes = totalMemoryBytes - freeMemoryBytes;
            var usagePercent = totalMemoryBytes > 0 ? (usedMemoryBytes / totalMemoryBytes) * 100 : 0;

            return new SystemMetric
            {
                Measurement = "memory",
                Time = DateTime.UtcNow,
                Tags = new Dictionary<string, string>
                {
                    ["server_name"] = _serverName,
                    ["environment"] = _serverEnvironment,
                    ["metric_type"] = "memory"
                },
                Fields = new Dictionary<string, double>
                {
                    ["total_gb"] = Math.Round(totalMemoryBytes / (1024.0 * 1024 * 1024), 2),
                    ["used_gb"] = Math.Round(usedMemoryBytes / (1024.0 * 1024 * 1024), 2),
                    ["free_gb"] = Math.Round(freeMemoryBytes / (1024.0 * 1024 * 1024), 2),
                    ["usage_percent"] = Math.Round(usagePercent, 2)
                }
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied collecting memory metrics");
            return null;
        }
    }

    /// <summary>
    /// Collects disk usage metrics (space + I/O) for all fixed drives.
    /// </summary>
    private List<SystemMetric> CollectDiskMetrics()
    {
        var metrics = new List<SystemMetric>();

        // Collect disk I/O via WMI (keyed by drive name like "C:")
        var diskIo = CollectDiskIoViaWmi();

        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed);

            foreach (var drive in drives)
            {
                try
                {
                    var totalBytes = drive.TotalSize;
                    var freeBytes = drive.AvailableFreeSpace;
                    var usedBytes = totalBytes - freeBytes;
                    var usagePercent = totalBytes > 0 ? (usedBytes / (double)totalBytes) * 100 : 0;
                    var driveLetter = drive.Name.TrimEnd('\\');

                    var fields = new Dictionary<string, double>
                    {
                        ["total_gb"] = Math.Round(totalBytes / (1024.0 * 1024 * 1024), 2),
                        ["used_gb"] = Math.Round(usedBytes / (1024.0 * 1024 * 1024), 2),
                        ["free_gb"] = Math.Round(freeBytes / (1024.0 * 1024 * 1024), 2),
                        ["usage_percent"] = Math.Round(usagePercent, 2)
                    };

                    // Merge I/O metrics if available for this drive
                    if (diskIo.TryGetValue(driveLetter, out var io))
                    {
                        foreach (var kv in io)
                            fields[kv.Key] = kv.Value;
                    }

                    metrics.Add(new SystemMetric
                    {
                        Measurement = "disk",
                        Time = DateTime.UtcNow,
                        Tags = new Dictionary<string, string>
                        {
                            ["server_name"] = _serverName,
                            ["environment"] = _serverEnvironment,
                            ["metric_type"] = "disk",
                            ["drive_letter"] = driveLetter,
                            ["drive_type"] = drive.DriveType.ToString(),
                            ["file_system"] = drive.DriveFormat
                        },
                        Fields = fields
                    });
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogWarning(ex, "Access denied reading drive {DriveName}", drive.Name);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "IO error reading drive {DriveName} (drive may be unavailable)", drive.Name);
                }
            }
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "IO error enumerating drives");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied enumerating drives");
        }

        return metrics;
    }

    /// <summary>
    /// Collects disk I/O performance via WMI. Returns a dictionary keyed by drive name (e.g. "C:").
    /// </summary>
    private Dictionary<string, Dictionary<string, double>> CollectDiskIoViaWmi()
    {
        var result = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DiskReadBytesPerSec, DiskWriteBytesPerSec, DiskReadsPerSec, DiskWritesPerSec, AvgDiskQueueLength " +
                "FROM Win32_PerfFormattedData_PerfDisk_LogicalDisk WHERE Name != '_Total'");

            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    var name = obj["Name"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;

                    var io = new Dictionary<string, double>();

                    if (obj["DiskReadBytesPerSec"] != null)
                        io["read_bytes_per_sec"] = Convert.ToDouble(obj["DiskReadBytesPerSec"]);
                    if (obj["DiskWriteBytesPerSec"] != null)
                        io["write_bytes_per_sec"] = Convert.ToDouble(obj["DiskWriteBytesPerSec"]);
                    if (obj["DiskReadsPerSec"] != null)
                        io["read_iops"] = Convert.ToDouble(obj["DiskReadsPerSec"]);
                    if (obj["DiskWritesPerSec"] != null)
                        io["write_iops"] = Convert.ToDouble(obj["DiskWritesPerSec"]);
                    if (obj["AvgDiskQueueLength"] != null)
                        io["queue_length"] = Convert.ToDouble(obj["AvgDiskQueueLength"]);

                    result[name] = io;
                }
            }
        }
        catch (ManagementException ex)
        {
            _logger.LogWarning(ex, "WMI disk I/O query failed (status: {Status})", ex.ErrorCode);
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "COM error in WMI disk I/O query (HRESULT: {HResult})", ex.HResult);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied for WMI disk I/O query");
        }

        return result;
    }

    /// <summary>
    /// Collects network interface metrics with delta calculation (bytes/sec).
    /// </summary>
    private List<SystemMetric> CollectNetworkMetrics()
    {
        var metrics = new List<SystemMetric>();
        var now = DateTime.UtcNow;

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                           n.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            foreach (var iface in interfaces)
            {
                try
                {
                    var stats = iface.GetIPv4Statistics();
                    var currentSent = stats.BytesSent;
                    var currentReceived = stats.BytesReceived;

                    var fields = new Dictionary<string, double>
                    {
                        ["bytes_sent_total"] = currentSent,
                        ["bytes_received_total"] = currentReceived,
                        ["speed_mbps"] = iface.Speed / 1_000_000.0
                    };

                    // Calculate deltas if we have a previous reading
                    if (_previousNetworkStats.TryGetValue(iface.Name, out var prev))
                    {
                        var elapsed = (now - prev.time).TotalSeconds;
                        if (elapsed > 0)
                        {
                            var sentDelta = currentSent - prev.bytesSent;
                            var recvDelta = currentReceived - prev.bytesReceived;

                            // Handle counter reset (e.g. after reboot)
                            if (sentDelta < 0) sentDelta = 0;
                            if (recvDelta < 0) recvDelta = 0;

                            fields["send_bytes_per_sec"] = Math.Round(sentDelta / elapsed, 2);
                            fields["recv_bytes_per_sec"] = Math.Round(recvDelta / elapsed, 2);
                        }
                    }

                    // Store current reading for next cycle
                    _previousNetworkStats[iface.Name] = (currentSent, currentReceived, now);

                    metrics.Add(new SystemMetric
                    {
                        Measurement = "network",
                        Time = now,
                        Tags = new Dictionary<string, string>
                        {
                            ["server_name"] = _serverName,
                            ["environment"] = _serverEnvironment,
                            ["metric_type"] = "network",
                            ["interface_name"] = iface.Name,
                            ["interface_type"] = iface.NetworkInterfaceType.ToString()
                        },
                        Fields = fields
                    });
                }
                catch (NetworkInformationException ex)
                {
                    _logger.LogWarning(ex, "Network error reading interface {InterfaceName} (error code: {ErrorCode})", iface.Name, ex.ErrorCode);
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning(ex, "Interface {InterfaceName} does not support IPv4 statistics", iface.Name);
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            _logger.LogError(ex, "Network error enumerating interfaces (error code: {ErrorCode})", ex.ErrorCode);
        }

        return metrics;
    }

    /// <summary>
    /// Disposes performance counters.
    /// </summary>
    public void Dispose()
    {
        if (_cpuCounter.IsValueCreated)
        {
            _cpuCounter.Value?.Dispose();
        }
    }
}
