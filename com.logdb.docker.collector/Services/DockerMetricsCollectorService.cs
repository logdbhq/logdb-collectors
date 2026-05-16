using System.Collections.Concurrent;
using Docker.DotNet;
using Docker.DotNet.Models;
using com.logdb.docker.collector.Configuration;
using com.logdb.docker.collector.Models;
using Microsoft.Extensions.Options;

namespace com.logdb.docker.collector.Services;

public class DockerMetricsCollectorService
{
    private readonly ILogger<DockerMetricsCollectorService> _logger;
    private readonly IDockerDiscoveryService _discovery;
    private readonly DockerMetricsOptions _options;
    private readonly DockerClient _dockerClient;
    private readonly SemaphoreSlim _statsSemaphore;
    private readonly ConcurrentDictionary<string, int> _failedStatsCount = new();
    private const int MaxConsecutiveFailures = 3;
    private const int FailureCooldownCycles = 10;

    // Latest snapshot for API access
    private volatile List<DockerMetricsRecord> _latestSnapshot = [];
    private DateTime? _lastCollectionUtc;
    private long _totalCollections;

    public IReadOnlyList<DockerMetricsRecord> LatestSnapshot => _latestSnapshot;
    public DateTime? LastCollectionUtc => _lastCollectionUtc;
    public long TotalCollections => Interlocked.Read(ref _totalCollections);

    public DockerMetricsCollectorService(
        ILogger<DockerMetricsCollectorService> logger,
        IDockerDiscoveryService discovery,
        IOptions<DockerMetricsOptions> options,
        IOptions<DockerDiscoveryOptions> discoveryOptions)
    {
        _logger = logger;
        _discovery = discovery;
        _options = options.Value;
        _statsSemaphore = new SemaphoreSlim(_options.MaxConcurrentStats, _options.MaxConcurrentStats);

        var endpoint = discoveryOptions.Value.DockerEndpoint ?? GetDefaultEndpoint();
        _dockerClient = new DockerClientConfiguration(new Uri(endpoint)).CreateClient();
    }

    public async Task<List<DockerMetricsRecord>> CollectAsync(CancellationToken cancellationToken)
    {
        var containers = _discovery.GetContainers();
        var targets = _options.IncludeStoppedContainers
            ? containers.Where(c => c.IsIncluded).ToList()
            : containers.Where(c => c.IsIncluded && c.State == "running").ToList();

        if (targets.Count == 0)
            return [];

        var tasks = new List<Task<DockerMetricsRecord?>>();

        foreach (var container in targets.Take(50))
        {
            var containerKey = container.Name;
            if (_failedStatsCount.TryGetValue(containerKey, out var failCount) && failCount >= MaxConsecutiveFailures)
            {
                if (failCount > MaxConsecutiveFailures)
                    _failedStatsCount[containerKey] = failCount - 1;

                _logger.LogDebug("Skipping metrics for {Container} (failed {Count} times, cooling down)", containerKey, failCount);
                continue;
            }

            tasks.Add(CollectContainerMetricsAsync(container, cancellationToken));
        }

        var results = await Task.WhenAll(tasks);
        var metrics = results.Where(r => r != null).Cast<DockerMetricsRecord>().ToList();

        _latestSnapshot = metrics;
        _lastCollectionUtc = DateTime.UtcNow;
        Interlocked.Increment(ref _totalCollections);

        return metrics;
    }

    private async Task<DockerMetricsRecord?> CollectContainerMetricsAsync(
        ContainerInfo container, CancellationToken cancellationToken)
    {
        await _statsSemaphore.WaitAsync(cancellationToken);
        try
        {
            var statsProgress = new Progress<ContainerStatsResponse>();
            ContainerStatsResponse? statsSnapshot = null;
            var tcs = new TaskCompletionSource<bool>();

            statsProgress.ProgressChanged += (_, stats) =>
            {
                if (statsSnapshot == null)
                {
                    statsSnapshot = stats;
                    tcs.TrySetResult(true);
                }
            };

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.StatsTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var statsTask = _dockerClient.Containers.GetContainerStatsAsync(
                container.Id,
                new ContainerStatsParameters { Stream = false },
                statsProgress,
                linkedCts.Token);

            try
            {
                await Task.WhenAny(tcs.Task, statsTask, Task.Delay(_options.StatsTimeoutSeconds * 1000, linkedCts.Token));
            }
            catch (OperationCanceledException)
            {
                TrackFailure(container.Name);
                return null;
            }

            try { await statsTask; } catch { /* handled via tcs or timeout */ }

            if (statsSnapshot == null)
            {
                TrackFailure(container.Name);
                return null;
            }

            // CPU calculation
            var cpuDelta = (double)(statsSnapshot.CPUStats.CPUUsage.TotalUsage - statsSnapshot.PreCPUStats.CPUUsage.TotalUsage);
            var systemDelta = (double)(statsSnapshot.CPUStats.SystemUsage - statsSnapshot.PreCPUStats.SystemUsage);
            var cpuPercent = 0.0;
            var onlineCpus = statsSnapshot.CPUStats.OnlineCPUs > 0
                ? (int)statsSnapshot.CPUStats.OnlineCPUs
                : statsSnapshot.CPUStats.CPUUsage.PercpuUsage?.Count ?? 1;

            if (systemDelta > 0 && cpuDelta > 0)
                cpuPercent = (cpuDelta / systemDelta) * onlineCpus * 100.0;

            // Memory
            var memoryUsage = (long)statsSnapshot.MemoryStats.Usage;
            var memoryLimit = (long)statsSnapshot.MemoryStats.Limit;
            var memoryPercent = memoryLimit > 0 ? (double)memoryUsage / memoryLimit * 100 : 0;

            // Network
            long rxBytes = 0, txBytes = 0, rxPackets = 0, txPackets = 0;
            if (statsSnapshot.Networks != null)
            {
                foreach (var network in statsSnapshot.Networks.Values)
                {
                    rxBytes += (long)network.RxBytes;
                    txBytes += (long)network.TxBytes;
                    rxPackets += (long)network.RxPackets;
                    txPackets += (long)network.TxPackets;
                }
            }

            // Block I/O
            long readBytes = 0, writeBytes = 0;
            if (statsSnapshot.BlkioStats?.IoServiceBytesRecursive != null)
            {
                foreach (var io in statsSnapshot.BlkioStats.IoServiceBytesRecursive)
                {
                    if (io.Op?.ToLower() == "read") readBytes += (long)io.Value;
                    else if (io.Op?.ToLower() == "write") writeBytes += (long)io.Value;
                }
            }

            // Health check (optional inspect)
            string? healthStatus = null;
            int restartCount = 0;
            if (_options.IncludeHealthCheck)
            {
                try
                {
                    var inspect = await _dockerClient.Containers.InspectContainerAsync(container.Id, cancellationToken);
                    healthStatus = inspect.State?.Health?.Status;
                    restartCount = (int)inspect.RestartCount;
                }
                catch
                {
                    // Non-critical
                }
            }

            _failedStatsCount.TryRemove(container.Name, out _);

            return new DockerMetricsRecord
            {
                Timestamp = DateTime.UtcNow,
                ContainerId = container.Id,
                ContainerName = container.Name,
                Image = container.Image,
                ImageTag = container.ImageTag,
                HostName = Environment.MachineName,
                ContainerState = container.State,
                ContainerStatus = container.Status,
                ComposeProject = container.ComposeProject,
                ComposeService = container.ComposeService,
                Labels = container.Labels,
                CpuUsagePercent = Math.Round(cpuPercent, 2),
                CpuTotalUsage = (long)statsSnapshot.CPUStats.CPUUsage.TotalUsage,
                CpuSystemUsage = (long)statsSnapshot.CPUStats.SystemUsage,
                CpuOnlineCpus = onlineCpus,
                MemoryUsageBytes = memoryUsage,
                MemoryLimitBytes = memoryLimit,
                MemoryUsagePercent = Math.Round(memoryPercent, 2),
                MemoryMaxUsageBytes = (long)statsSnapshot.MemoryStats.MaxUsage,
                NetworkRxBytes = rxBytes,
                NetworkTxBytes = txBytes,
                NetworkRxPackets = rxPackets,
                NetworkTxPackets = txPackets,
                BlockIoReadBytes = readBytes,
                BlockIoWriteBytes = writeBytes,
                PidsCurrent = (int)statsSnapshot.PidsStats.Current,
                HealthStatus = healthStatus,
                RestartCount = restartCount
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get metrics for container {Id}", container.Id);
            TrackFailure(container.Name);
            return null;
        }
        finally
        {
            _statsSemaphore.Release();
        }
    }

    private void TrackFailure(string containerKey)
    {
        var count = _failedStatsCount.AddOrUpdate(containerKey, 1, (_, c) => c + 1);
        if (count == MaxConsecutiveFailures)
        {
            _failedStatsCount[containerKey] = MaxConsecutiveFailures + FailureCooldownCycles;
            _logger.LogWarning("Container {Container} failed stats {Count} times, pausing for {Cooldown} cycles",
                containerKey, MaxConsecutiveFailures, FailureCooldownCycles);
        }
    }

    private static string GetDefaultEndpoint() =>
        OperatingSystem.IsWindows()
            ? "npipe://./pipe/docker_engine"
            : "unix:///var/run/docker.sock";
}
