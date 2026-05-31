using com.logdb.docker.collector.Configuration;
using com.logdb.docker.collector.Models;
using com.logdb.docker.collector.Security;
using com.logdb.docker.collector.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Map LOGDB_* env vars to configuration sections
var envOverrides = new Dictionary<string, string?>();
void MapEnv(string envVar, string configKey)
{
    var value = Environment.GetEnvironmentVariable(envVar);
    if (value is not null) envOverrides[configKey] = value;
}

MapEnv("LOGDB_EXPORTER_ENDPOINT", "LogDbExporter:Endpoint");
MapEnv("LOGDB_EXPORTER_APIKEY", "LogDbExporter:ApiKey");
MapEnv("LOGDB_EXPORTER_ENABLED", "LogDbExporter:Enabled");
MapEnv("LOGDB_EXPORTER_MAX_RETRIES", "LogDbExporter:MaxRetries");
MapEnv("LOGDB_EXPORTER_TIMEOUT", "LogDbExporter:RequestTimeoutSeconds");
MapEnv("LOGDB_EXPORTER_PROTOCOL", "LogDbExporter:Protocol");
MapEnv("LOGDB_EXPORTER_COMPRESSION", "LogDbExporter:EnableCompression");
var spoolMaxMb = Environment.GetEnvironmentVariable("LOGDB_SPOOL_MAX_DISK_MB");
if (spoolMaxMb is not null && long.TryParse(spoolMaxMb, out var mb))
    envOverrides["Spool:MaxDiskBytes"] = (mb * 1_048_576).ToString();
MapEnv("LOGDB_DISCOVERY_INTERVAL", "DockerDiscovery:RefreshIntervalSeconds");
MapEnv("LOGDB_CHECKPOINT_FLUSH_INTERVAL", "Checkpoint:FlushIntervalSeconds");
MapEnv("LOGDB_METRICS_ENABLED", "DockerMetrics:Enabled");
MapEnv("LOGDB_METRICS_INTERVAL", "DockerMetrics:CollectionIntervalSeconds");
MapEnv("LOGDB_TAIL_ACTIVE_INTERVAL", "Tail:ActiveIntervalSeconds");
MapEnv("LOGDB_TAIL_IDLE_INTERVAL", "Tail:IdleIntervalSeconds");
MapEnv("LOGDB_API_KEY", "Auth:ApiKey");

if (envOverrides.Count > 0)
    builder.Configuration.AddInMemoryCollection(envOverrides);

// Resolve gRPC service URL via discovery when Endpoint is not explicitly configured.
// Runs regardless of Enabled so that toggling the exporter on later finds a real endpoint.
// Graceful: on failure, the spool absorbs logs until the operator fixes discovery or the endpoint.
{
    var configuredEndpoint = builder.Configuration["LogDbExporter:Endpoint"];
    if (EndpointDiscovery.IsPlaceholder(configuredEndpoint))
    {
        var apiKey = builder.Configuration["LogDbExporter:ApiKey"];
        var resolved = await EndpointDiscovery.DiscoverGrpcLoggerUrlAsync(apiKey, Console.WriteLine);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            builder.Configuration["LogDbExporter:Endpoint"] = resolved;
        }
    }
}

builder.Services.Configure<DockerDiscoveryOptions>(
    builder.Configuration.GetSection(DockerDiscoveryOptions.Section));
builder.Services.Configure<CollectorFilterOptions>(
    builder.Configuration.GetSection(CollectorFilterOptions.Section));
builder.Services.Configure<CheckpointOptions>(
    builder.Configuration.GetSection(CheckpointOptions.Section));
builder.Services.Configure<LogDbExporterOptions>(
    builder.Configuration.GetSection(LogDbExporterOptions.Section));
builder.Services.Configure<SpoolOptions>(
    builder.Configuration.GetSection(SpoolOptions.Section));
builder.Services.Configure<DockerMetricsOptions>(
    builder.Configuration.GetSection(DockerMetricsOptions.Section));
builder.Services.Configure<TailOptions>(
    builder.Configuration.GetSection(TailOptions.Section));

builder.Services.AddSingleton<StartupValidator>();
builder.Services.AddSingleton<AgentStatusService>();
builder.Services.AddSingleton<ContainerToggleService>();
builder.Services.AddSingleton<ContainerFilterService>();
builder.Services.AddSingleton<FilterRuleService>();
builder.Services.AddSingleton<LiveConsoleBuffer>();
builder.Services.AddSingleton<DeliveryConsoleBuffer>();
builder.Services.AddSingleton<DeliveryActivityTracker>();
builder.Services.AddSingleton<SpoolReplayState>();
builder.Services.AddSingleton<SpoolReplayTrigger>();
builder.Services.AddSingleton<ICheckpointStore, FileCheckpointStore>();
builder.Services.AddSingleton<IDockerDiscoveryService, DockerDiscoveryService>();
builder.Services.AddSingleton<ISpoolStore, FileSpoolStore>();
builder.Services.AddSingleton<ILogRecordSink, LogPipelineService>();
builder.Services.AddSingleton<ILogDbExporter, LogDbExporterService>();
builder.Services.AddSingleton<IFileTailService, DockerFileTailService>();
builder.Services.AddHostedService<DockerDiscoveryWorker>();
builder.Services.AddHostedService<FileTailWorker>();
builder.Services.AddHostedService<CheckpointFlushWorker>();
builder.Services.AddHostedService<SpoolReplayWorker>();
builder.Services.AddSingleton<MetricsSettingsService>();
builder.Services.AddSingleton<MetricsSpoolStore>();
builder.Services.AddSingleton<DockerMetricsCollectorService>();
builder.Services.AddHostedService<DockerMetricsWorker>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

app.UseCors();

if (string.IsNullOrEmpty(app.Configuration["Auth:ApiKey"]))
    app.Logger.LogWarning("LOGDB_API_KEY not set — collector API is unauthenticated.");

// Require the shared API key on every endpoint except health probes.
app.UseApiKeyAuth();

// Health endpoints
app.MapGet("/health", () => new { status = "ok" });
app.MapGet("/health/live", () => new { status = "ok" });
app.MapGet("/health/ready", (AgentStatusService statusService) =>
{
    var result = statusService.GetReadiness();
    return result.Ready
        ? Results.Ok(result)
        : Results.Json(result, statusCode: 503);
});

app.MapGet("/api/status", (AgentStatusService statusService) => statusService.GetStatus());

app.MapGet("/api/docker/status", (IDockerDiscoveryService discovery) => discovery.GetDockerStatus());

app.MapGet("/api/containers", (IDockerDiscoveryService discovery) => discovery.GetContainers());

app.MapGet("/api/containers/{id}/toggle", (string id, ContainerToggleService toggle) =>
    new { containerId = id, enabled = !toggle.IsDisabled(id) });

app.MapPost("/api/containers/{id}/toggle", async (string id, ContainerToggleService toggle, IDockerDiscoveryService discovery) =>
{
    var isCurrentlyDisabled = toggle.IsDisabled(id);
    var newEnabled = isCurrentlyDisabled; // flip
    toggle.SetEnabled(id, newEnabled);

    // Re-run discovery so filters are re-applied immediately
    await discovery.RefreshAsync();

    return new { containerId = id, enabled = newEnabled };
});

app.MapPost("/api/containers/toggle-all", async (ToggleAllRequest request, ContainerToggleService toggle, IDockerDiscoveryService discovery) =>
{
    var containers = discovery.GetContainers();
    var runningIds = containers
        .Where(c => c.State.Equals("running", StringComparison.OrdinalIgnoreCase))
        .Select(c => c.Id)
        .ToList();
    toggle.SetAllEnabled(runningIds, request.Enabled);
    await discovery.RefreshAsync();
    return new { count = runningIds.Count, enabled = request.Enabled };
});

app.MapPost("/api/containers/log-mode-all", async (LogModeAllRequest request, ContainerToggleService toggle, IDockerDiscoveryService discovery) =>
{
    var containers = discovery.GetContainers();
    var runningIds = containers
        .Where(c => c.State.Equals("running", StringComparison.OrdinalIgnoreCase))
        .Select(c => c.Id)
        .ToList();
    var mode = request.LogMode.Equals("errors", StringComparison.OrdinalIgnoreCase) ? LogMode.ErrorsOnly : LogMode.All;
    toggle.SetAllLogModes(runningIds, mode);
    return new { count = runningIds.Count, logMode = mode.ToString().ToLowerInvariant() };
});

app.MapGet("/api/containers/{id}/log-mode", (string id, ContainerToggleService toggle) =>
    new { containerId = id, logMode = toggle.GetLogMode(id).ToString().ToLowerInvariant() });

app.MapPost("/api/containers/{id}/log-mode", (string id, ContainerToggleService toggle) =>
{
    var newMode = toggle.ToggleLogMode(id);
    return new { containerId = id, logMode = newMode.ToString().ToLowerInvariant() };
});

app.MapGet("/api/containers/{id}/start-date", (string id, ContainerToggleService toggle) =>
    new { containerId = id, startDate = toggle.GetContainerStartDate(id)?.ToString("o") });

app.MapPost("/api/containers/{id}/start-date", (string id, StartDateRequest request, ContainerToggleService toggle) =>
{
    toggle.SetContainerStartDate(id, request.StartDate);
    return new { containerId = id, startDate = request.StartDate?.ToString("o") };
});

app.MapGet("/api/start-date/global", (ContainerToggleService toggle) =>
    new { startDate = toggle.GetGlobalStartDate()?.ToString("o") });

app.MapPost("/api/start-date/global", (StartDateRequest request, ContainerToggleService toggle) =>
{
    toggle.SetGlobalStartDate(request.StartDate);
    return new { startDate = request.StartDate?.ToString("o") };
});

app.MapPost("/api/pipeline/estimate-size", (EstimateSizeRequest request, IFileTailService tail) =>
{
    if (request.FromUtc is null)
        return Results.BadRequest(new { error = "fromUtc is required" });

    var estimate = tail.EstimateSize(request.LogPath, request.FromUtc.Value);
    return Results.Ok(estimate);
});

app.MapGet("/api/pipeline/targets", (IFileTailService tail) => tail.GetTargets());

app.MapGet("/api/pipeline/status", (IFileTailService tail) => tail.GetPipelineStatus());

app.MapPost("/api/pipeline/clear", (ICheckpointStore checkpoints, ISpoolStore spool, IFileTailService tail) =>
{
    checkpoints.Clear();
    spool.Clear();
    tail.ResetOffsets();
    return new { status = "cleared" };
});

app.MapPost("/api/pipeline/clear/{containerId}", (string containerId, ICheckpointStore checkpoints, IFileTailService tail) =>
{
    checkpoints.ClearForContainer(containerId);
    tail.ResetOffset(containerId);
    return new { status = "cleared", containerId };
});

app.MapGet("/api/checkpoints", (ICheckpointStore store) => store.GetCheckpoints());

app.MapGet("/api/checkpoints/status", (ICheckpointStore store) => store.GetStatus());

app.MapGet("/api/exporter/status", (ILogDbExporter exporter) => exporter.GetStatus());

// Per-record view of what the exporter handed to grpc-logger (logs + metrics), with delivery outcome.
app.MapGet("/api/exporter/sent/recent", (DeliveryConsoleBuffer buffer, int? count, string? outcome, string? kind, string? filter) =>
    buffer.GetRecent(count ?? 200,
        string.IsNullOrWhiteSpace(outcome) ? null : outcome,
        string.IsNullOrWhiteSpace(kind) ? null : kind,
        string.IsNullOrWhiteSpace(filter) ? null : filter));

// Time-series of records sent to grpc-logger (delivered/failed + batches, plus metrics), for the Activity chart.
app.MapGet("/api/exporter/activity", (DeliveryActivityTracker activity, int? minutes) =>
    activity.GetActivity(minutes ?? 60, DateTime.UtcNow));

// Countdown to the next spool replay (log batch send) cycle.
app.MapGet("/api/exporter/replay-status", (SpoolReplayState state) => state.GetSnapshot());

// Manually trigger an immediate replay cycle (send now) instead of waiting for the interval.
app.MapPost("/api/exporter/flush-now", (SpoolReplayTrigger trigger) =>
{
    trigger.RequestFlush();
    return Results.Ok(new { requested = true });
});

app.MapPost("/api/exporter/toggle", (ILogDbExporter exporter) =>
{
    var current = exporter.GetStatus().Enabled;
    exporter.SetEnabled(!current);
    return new { enabled = !current };
});

app.MapGet("/api/spool/status", (ISpoolStore spool) => spool.GetStatus());

app.MapPost("/api/spool/max-size", (SpoolSizeRequest request, ISpoolStore spool) =>
{
    if (request.MaxSizeMb < 10 || request.MaxSizeMb > 10_000)
        return Results.BadRequest(new { error = "maxSizeMb must be between 10 and 10000" });

    var bytes = request.MaxSizeMb * 1_048_576L;
    spool.SetMaxDiskBytes(bytes);
    spool.EnforceLimit();
    var status = spool.GetStatus();
    return Results.Ok(new { maxSizeMb = request.MaxSizeMb, maxDiskBytes = bytes, currentUsageMb = status.DiskBytesUsed / 1_048_576.0 });
});

// Filters
app.MapGet("/api/filters", (FilterRuleService filters) => filters.GetRules());

app.MapPost("/api/filters/exclude-message", (FilterAddRequest request, FilterRuleService filters) =>
    filters.AddMessagePattern(request.Value.Trim()));

app.MapDelete("/api/filters/exclude-message", (string value, FilterRuleService filters) =>
    filters.RemoveMessagePattern(value));

app.MapPost("/api/filters/exclude-category", (FilterAddRequest request, FilterRuleService filters) =>
    filters.AddCategory(request.Value.Trim()));

app.MapDelete("/api/filters/exclude-category", (string value, FilterRuleService filters) =>
    filters.RemoveCategory(value));

app.MapGet("/api/dashboard", (
    AgentStatusService agentStatus,
    IDockerDiscoveryService discovery,
    IFileTailService tail,
    ILogDbExporter exporter,
    ISpoolStore spool,
    MetricsSpoolStore metricsSpool,
    FilterRuleService filters) =>
{
    var docker = discovery.GetDockerStatus();
    var containers = discovery.GetContainers();
    var pipeline = tail.GetPipelineStatus();
    var exp = exporter.GetStatus();
    var sp = spool.GetStatus();

    return new DashboardSummary
    {
        Agent = agentStatus.GetStatus(),
        Docker = new DockerSummary
        {
            Available = docker.Available,
            Endpoint = docker.Endpoint,
            ContainerCount = docker.ContainerCount,
            IncludedCount = containers.Count(c => c.IsIncluded),
            Error = docker.Error
        },
        Pipeline = new PipelineSummary
        {
            ActiveTargets = pipeline.ActiveTargets,
            RecordsRead = pipeline.RecordsRead,
            ParseErrors = pipeline.ParseErrors,
            ReadErrors = pipeline.ReadErrors,
            FilteredByMessage = filters.FilteredByMessage,
            FilteredByCategory = filters.FilteredByCategory,
            LastRecordTimestamp = pipeline.LastRecordTimestamp
        },
        Exporter = new ExporterSummary
        {
            Enabled = exp.Enabled,
            Healthy = exp.Healthy,
            BatchesSent = exp.BatchesSent,
            RecordsSent = exp.RecordsSent,
            MetricsBatchesSent = exp.MetricsBatchesSent,
            MetricsRecordsSent = exp.MetricsRecordsSent,
            SendErrors = exp.SendErrors,
            LastSendUtc = exp.LastSendUtc,
            LastError = exp.LastError
        },
        Spool = new SpoolSummary
        {
            Enabled = sp.Enabled,
            QueuedRecords = sp.QueuedRecords,
            DiskBytesUsed = sp.DiskBytesUsed,
            MaxDiskBytes = sp.MaxDiskBytes,
            UtilizationPercent = sp.UtilizationPercent,
            DroppedRecords = sp.DroppedRecords,
            ReplayedRecords = sp.ReplayedRecords,
            LastError = sp.LastError
        },
        MetricsSpool = new MetricsSpoolSummary
        {
            QueuedRecords = metricsSpool.QueuedRecords,
            DroppedRecords = metricsSpool.DroppedRecords,
            ReplayedRecords = metricsSpool.ReplayedRecords
        }
    };
});

app.MapGet("/api/console/recent", (LiveConsoleBuffer buffer, ContainerToggleService toggles,
    FilterRuleService filters, int? count, string? container) =>
{
    var snapshot = buffer.GetRecent(count ?? 200, container);

    // Mark each entry with export status
    foreach (var entry in snapshot.Records)
    {
        entry.Exported = toggles.ShouldExport(entry.ContainerId, entry.Stream, entry.ParsedLevel)
            && !filters.ShouldExcludeConsoleEntry(entry.Message, entry.Category);
    }

    return snapshot;
});

// Docker Metrics API
app.MapGet("/api/metrics/live", (DockerMetricsCollectorService metrics) => new
{
    Containers = metrics.LatestSnapshot.Select(m => new
    {
        m.ContainerId,
        m.ContainerName,
        m.Image,
        m.ImageTag,
        m.ContainerState,
        m.ContainerStatus,
        m.ComposeProject,
        m.ComposeService,
        m.CpuUsagePercent,
        m.CpuOnlineCpus,
        m.MemoryUsageBytes,
        m.MemoryLimitBytes,
        m.MemoryUsagePercent,
        m.NetworkRxBytes,
        m.NetworkTxBytes,
        m.BlockIoReadBytes,
        m.BlockIoWriteBytes,
        m.PidsCurrent,
        m.HealthStatus,
        m.RestartCount,
        m.Timestamp
    }),
    TotalCpu = metrics.LatestSnapshot.Sum(m => m.CpuUsagePercent),
    TotalMemoryUsed = metrics.LatestSnapshot.Sum(m => m.MemoryUsageBytes),
    TotalMemoryLimit = metrics.LatestSnapshot.Any() ? metrics.LatestSnapshot.Max(m => m.MemoryLimitBytes) : 0,
    TotalNetworkRx = metrics.LatestSnapshot.Sum(m => m.NetworkRxBytes),
    TotalNetworkTx = metrics.LatestSnapshot.Sum(m => m.NetworkTxBytes),
    ContainerCount = metrics.LatestSnapshot.Count,
    LastCollectionUtc = metrics.LastCollectionUtc,
    TotalCollections = metrics.TotalCollections
});

app.MapGet("/api/metrics/settings", (MetricsSettingsService settings) => new
{
    intervalSeconds = settings.IntervalSeconds,
    minIntervalSeconds = MetricsSettingsService.MinIntervalSeconds,
    maxIntervalSeconds = MetricsSettingsService.MaxIntervalSeconds
});

app.MapPost("/api/metrics/settings", (MetricsSettingsRequest request, MetricsSettingsService settings) =>
{
    if (request.IntervalSeconds < MetricsSettingsService.MinIntervalSeconds ||
        request.IntervalSeconds > MetricsSettingsService.MaxIntervalSeconds)
    {
        return Results.BadRequest(new
        {
            error = $"intervalSeconds must be between {MetricsSettingsService.MinIntervalSeconds} and {MetricsSettingsService.MaxIntervalSeconds}"
        });
    }

    settings.SetIntervalSeconds(request.IntervalSeconds);
    return Results.Ok(new { intervalSeconds = settings.IntervalSeconds });
});

// Discovery: resolve portal base URL for UI "Systems" sidebar links
app.MapGet("/api/discovery/portal", async (IOptions<LogDbExporterOptions> exporterOpts) =>
{
    var apiKey = exporterOpts.Value.ApiKey;
    if (string.IsNullOrEmpty(apiKey))
        return Results.Ok(new { portalUrl = (string?)null, error = "No API key configured" });

    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://discovery.logdb.site/resolve/web-portal");
        request.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
        var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var url = doc.RootElement.TryGetProperty("serviceUrl", out var prop) ? prop.GetString() : null;
        return Results.Ok(new { portalUrl = url?.TrimEnd('/') });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { portalUrl = (string?)null, error = ex.Message });
    }
});

// Startup validation
var validator = app.Services.GetRequiredService<StartupValidator>();
validator.Validate(
    app.Services.GetRequiredService<IOptions<LogDbExporterOptions>>(),
    app.Services.GetRequiredService<IOptions<CheckpointOptions>>(),
    app.Services.GetRequiredService<IOptions<SpoolOptions>>(),
    app.Services.GetRequiredService<IOptions<DockerDiscoveryOptions>>(),
    app.Services.GetRequiredService<ILogger<StartupValidator>>());

// Initialize stores before starting workers
var checkpointStore = app.Services.GetRequiredService<ICheckpointStore>();
checkpointStore.Load();

var spoolStore = app.Services.GetRequiredService<ISpoolStore>();
spoolStore.Initialize();

var metricsSpoolStore = app.Services.GetRequiredService<MetricsSpoolStore>();
metricsSpoolStore.Initialize();

app.Run();
