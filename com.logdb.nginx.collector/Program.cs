using com.logdb.nginx.collector.Configuration;
using com.logdb.nginx.collector.Models;
using com.logdb.nginx.collector.Services;
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
MapEnv("LOGDB_CHECKPOINT_FLUSH_INTERVAL", "Checkpoint:FlushIntervalSeconds");
MapEnv("LOGDB_NGINX_BLOCK_ENABLED", "NginxBlock:Enabled");
MapEnv("LOGDB_NGINX_DENY_FILE", "NginxBlock:DenyFilePath");
MapEnv("LOGDB_NGINX_RELOAD_CMD", "NginxBlock:ReloadCommand");
MapEnv("LOGDB_TAIL_ACTIVE_INTERVAL", "Tail:ActiveIntervalSeconds");
MapEnv("LOGDB_TAIL_IDLE_INTERVAL", "Tail:IdleIntervalSeconds");

if (envOverrides.Count > 0)
    builder.Configuration.AddInMemoryCollection(envOverrides);

// Resolve gRPC service URL via discovery when Endpoint is not explicitly configured.
// Graceful: on failure, keep whatever Endpoint is set — the spool will absorb logs
// until the operator fixes either discovery or the explicit endpoint.
if (builder.Configuration.GetValue<bool>("LogDbExporter:Enabled"))
{
    var configuredEndpoint = builder.Configuration["LogDbExporter:Endpoint"];
    if (string.IsNullOrWhiteSpace(configuredEndpoint)
        || configuredEndpoint.Contains("your-service.com", StringComparison.OrdinalIgnoreCase)
        || configuredEndpoint.Contains("localhost", StringComparison.OrdinalIgnoreCase))
    {
        var resolved = await DiscoverServiceUrlAsync(builder.Configuration);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            builder.Configuration["LogDbExporter:Endpoint"] = resolved;
        }
    }
}

builder.Services.Configure<NginxTargetOptions>(
    builder.Configuration.GetSection(NginxTargetOptions.Section));
builder.Services.Configure<CheckpointOptions>(
    builder.Configuration.GetSection(CheckpointOptions.Section));
builder.Services.Configure<LogDbExporterOptions>(
    builder.Configuration.GetSection(LogDbExporterOptions.Section));
builder.Services.Configure<SpoolOptions>(
    builder.Configuration.GetSection(SpoolOptions.Section));
builder.Services.Configure<TailOptions>(
    builder.Configuration.GetSection(TailOptions.Section));
builder.Services.Configure<NginxBlockOptions>(
    builder.Configuration.GetSection(NginxBlockOptions.Section));

builder.Services.AddSingleton<NginxIpBlockService>();
builder.Services.AddSingleton<TargetToggleService>();
builder.Services.AddSingleton<FilterRuleService>();
builder.Services.AddSingleton<LiveConsoleBuffer>();
builder.Services.AddSingleton<ExporterConsoleBuffer>();
builder.Services.AddSingleton<StartupValidator>();
builder.Services.AddSingleton<AgentStatusService>();
builder.Services.AddSingleton<ICheckpointStore, FileCheckpointStore>();
builder.Services.AddSingleton<ISpoolStore, FileSpoolStore>();
builder.Services.AddSingleton<ILogRecordSink, LogPipelineService>();
builder.Services.AddSingleton<ILogDbExporter, LogDbExporterService>();
builder.Services.AddSingleton<IFileTailService, NginxFileTailService>();
builder.Services.AddHostedService<FileTailWorker>();
builder.Services.AddHostedService<CheckpointFlushWorker>();
builder.Services.AddHostedService<SpoolReplayWorker>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

app.UseCors();

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

// Status
app.MapGet("/api/status", (AgentStatusService statusService) => statusService.GetStatus());

// Targets
app.MapGet("/api/targets", (IOptions<NginxTargetOptions> opts, TargetToggleService toggle) =>
{
    var overrides = toggle.GetAllOverrides();
    return opts.Value.Targets.Select(t =>
    {
        overrides.TryGetValue(t.Name, out var o);
        return new
        {
            t.Name,
            t.AccessLogPath,
            t.ErrorLogPath,
            t.Enabled,
            AccessLogEnabled = o?.AccessLogEnabled ?? true,
            ErrorLogEnabled = o?.ErrorLogEnabled ?? true,
            AccessLogExists = !string.IsNullOrEmpty(t.AccessLogPath) && File.Exists(t.AccessLogPath),
            ErrorLogExists = !string.IsNullOrEmpty(t.ErrorLogPath) && File.Exists(t.ErrorLogPath)
        };
    });
});

app.MapPost("/api/targets/{name}/access-log", (string name, ToggleRequest request, TargetToggleService toggle) =>
{
    toggle.SetAccessLogEnabled(name, request.Enabled);
    return new { target = name, accessLogEnabled = request.Enabled };
});

app.MapPost("/api/targets/{name}/error-log", (string name, ToggleRequest request, TargetToggleService toggle) =>
{
    toggle.SetErrorLogEnabled(name, request.Enabled);
    return new { target = name, errorLogEnabled = request.Enabled };
});

app.MapPost("/api/targets/{name}/toggle", (string name, ToggleRequest request, TargetToggleService toggle) =>
{
    toggle.SetTargetEnabled(name, request.Enabled);
    return new { target = name, enabled = request.Enabled };
});

// Pipeline
app.MapGet("/api/pipeline/targets", (IFileTailService tail) => tail.GetTargets());
app.MapGet("/api/pipeline/status", (IFileTailService tail) => tail.GetPipelineStatus());

// Checkpoints
app.MapGet("/api/checkpoints", (ICheckpointStore store) => store.GetCheckpoints());
app.MapGet("/api/checkpoints/status", (ICheckpointStore store) => store.GetStatus());

// Exporter
app.MapGet("/api/exporter/status", (ILogDbExporter exporter) => exporter.GetStatus());
app.MapGet("/api/exporter/console/recent", (ExporterConsoleBuffer buffer, int? count, string? outcome) =>
    buffer.GetRecent(count ?? 200, outcome));

// Live console
app.MapGet("/api/console/recent", (LiveConsoleBuffer buffer, int? count, string? filter) =>
    buffer.GetRecent(count ?? 200, filter));

// Log tail (unfiltered, reads directly from files)
app.MapGet("/api/tail/recent", (IFileTailService tail, int? lines) =>
    tail.ReadRecentLines(lines ?? 200));
app.MapPost("/api/exporter/toggle", (ToggleRequest request, ILogDbExporter exporter) =>
{
    exporter.SetEnabled(request.Enabled);
    return new { enabled = request.Enabled };
});
app.MapPost("/api/exporter/flush-interval", (FlushIntervalRequest request, ILogDbExporter exporter) =>
{
    var status = exporter.GetStatus();
    if (request.Seconds < status.FlushIntervalMinSeconds || request.Seconds > status.FlushIntervalMaxSeconds)
        return Results.BadRequest(new
        {
            error = $"seconds must be between {status.FlushIntervalMinSeconds} and {status.FlushIntervalMaxSeconds}"
        });

    exporter.SetFlushIntervalSeconds(request.Seconds);
    return Results.Ok(new { flushIntervalSeconds = exporter.FlushIntervalSeconds });
});

// Filters
app.MapGet("/api/filters", (FilterRuleService filters) => filters.GetRules());
app.MapPost("/api/filters/exclude-path", (FilterAddRequest request, FilterRuleService filters) =>
{
    if (string.IsNullOrWhiteSpace(request.Value))
        return Results.BadRequest(new { error = "value is required" });
    filters.AddExcludePath(request.Value.Trim());
    return Results.Ok(filters.GetRules());
});
app.MapDelete("/api/filters/exclude-path", (string value, FilterRuleService filters) =>
{
    filters.RemoveExcludePath(value);
    return filters.GetRules();
});
app.MapPost("/api/filters/exclude-ip", (FilterAddRequest request, FilterRuleService filters) =>
{
    if (string.IsNullOrWhiteSpace(request.Value))
        return Results.BadRequest(new { error = "value is required" });
    filters.AddExcludeRemoteAddress(request.Value.Trim());
    return Results.Ok(filters.GetRules());
});
app.MapDelete("/api/filters/exclude-ip", (string value, FilterRuleService filters) =>
{
    filters.RemoveExcludeRemoteAddress(value);
    return filters.GetRules();
});

// Nginx IP blocking
app.MapGet("/api/nginx/blocked-ips", (NginxIpBlockService blocker) => new
{
    enabled = blocker.IsEnabled,
    blockedIps = blocker.GetBlockedIps()
});
app.MapPost("/api/nginx/block-ip", (FilterAddRequest request, NginxIpBlockService blocker) =>
{
    if (string.IsNullOrWhiteSpace(request.Value))
        return Results.BadRequest(new { error = "value is required" });
    var (success, error) = blocker.BlockIp(request.Value.Trim());
    return success
        ? Results.Ok(new { blocked = true, ip = request.Value.Trim(), blockedIps = blocker.GetBlockedIps() })
        : Results.Json(new { blocked = false, error }, statusCode: 500);
});
app.MapDelete("/api/nginx/block-ip", (string value, NginxIpBlockService blocker) =>
{
    var (success, error) = blocker.UnblockIp(value);
    return success
        ? Results.Ok(new { unblocked = true, ip = value, blockedIps = blocker.GetBlockedIps() })
        : Results.Json(new { unblocked = false, error }, statusCode: 500);
});

// Spool
app.MapGet("/api/spool/status", (ISpoolStore spool) => spool.GetStatus());
app.MapDelete("/api/spool/clear", (ISpoolStore spool) =>
{
    spool.Clear();
    return spool.GetStatus();
});
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

// Dashboard
app.MapGet("/api/dashboard", (
    AgentStatusService agentStatus,
    IOptions<NginxTargetOptions> targetOpts,
    IFileTailService tail,
    ILogDbExporter exporter,
    ISpoolStore spool) =>
{
    var targets = targetOpts.Value;
    var pipeline = tail.GetPipelineStatus();
    var exp = exporter.GetStatus();
    var sp = spool.GetStatus();
    var tailTargets = tail.GetTargets();

    return new DashboardSummary
    {
        Agent = agentStatus.GetStatus(),
        Targets = new TargetsSummary
        {
            ConfiguredTargets = targets.Targets.Count,
            EnabledTargets = targets.Targets.Count(t => t.Enabled),
            ActiveFiles = tailTargets.Count(t => File.Exists(t.FilePath)),
            MissingFiles = tailTargets.Where(t => !File.Exists(t.FilePath)).Select(t => t.FilePath).ToList()
        },
        Pipeline = new PipelineSummary
        {
            ActiveTargets = pipeline.ActiveTargets,
            ActiveFiles = pipeline.ActiveFiles,
            AccessRecordsRead = pipeline.AccessRecordsRead,
            ErrorRecordsRead = pipeline.ErrorRecordsRead,
            ParseErrors = pipeline.ParseErrors,
            ReadErrors = pipeline.ReadErrors,
            FilteredStaticFiles = pipeline.FilteredStaticFiles,
            FilteredByRules = pipeline.FilteredByRules,
            RotationsDetected = pipeline.RotationsDetected,
            LastRecordTimestamp = pipeline.LastRecordTimestamp,
            LastTailCycleUtc = pipeline.LastTailCycleUtc
        },
        Exporter = new ExporterSummary
        {
            Enabled = exp.Enabled,
            Endpoint = exp.Endpoint,
            ApiKeyConfigured = exp.ApiKeyConfigured,
            Healthy = exp.Healthy,
            BatchesSent = exp.BatchesSent,
            RecordsSent = exp.RecordsSent,
            SendErrors = exp.SendErrors,
            RetryCount = exp.RetryCount,
            LastSendUtc = exp.LastSendUtc,
            LastError = exp.LastError,
            FlushIntervalSeconds = exp.FlushIntervalSeconds,
            FlushIntervalMinSeconds = exp.FlushIntervalMinSeconds,
            FlushIntervalMaxSeconds = exp.FlushIntervalMaxSeconds
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
        }
    };
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

// Build metadata from environment (set in Dockerfile)
var agentStatus = app.Services.GetRequiredService<AgentStatusService>();
agentStatus.SetBuildInfo(
    Environment.GetEnvironmentVariable("LOGDB_BUILD_DATE") ?? "",
    Environment.GetEnvironmentVariable("LOGDB_COMMIT_HASH") ?? "",
    app.Environment.EnvironmentName);

// Startup validation
var validator = app.Services.GetRequiredService<StartupValidator>();
validator.Validate(
    app.Services.GetRequiredService<IOptions<LogDbExporterOptions>>(),
    app.Services.GetRequiredService<IOptions<CheckpointOptions>>(),
    app.Services.GetRequiredService<IOptions<SpoolOptions>>(),
    app.Services.GetRequiredService<IOptions<NginxTargetOptions>>(),
    app.Services.GetRequiredService<ILogger<StartupValidator>>());

// Initialize stores before starting workers
var checkpointStore = app.Services.GetRequiredService<ICheckpointStore>();
checkpointStore.Load();
agentStatus.SetCheckpointInitialized();

var spoolStore = app.Services.GetRequiredService<ISpoolStore>();
spoolStore.Initialize();
agentStatus.SetSpoolInitialized();

app.Run();

static async Task<string?> DiscoverServiceUrlAsync(IConfiguration configuration)
{
    try
    {
        Console.WriteLine("Discovering LogDB service URL from discovery service...");
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var apiKey = configuration["LogDbExporter:ApiKey"];
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://discovery.logdb.site/resolve/grpc-logger");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
        }

        using var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("serviceUrl", out var prop))
        {
            var discoveredUrl = prop.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(discoveredUrl))
            {
                Console.WriteLine($"Discovered LogDB service URL: {discoveredUrl}");
                return discoveredUrl;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Discovery service failed: {ex.Message} (falling back to configured endpoint)");
    }
    return null;
}
