using com.logdb.windows.collector.Configuration;
using com.logdb.windows.collector.Health;
using com.logdb.windows.collector.Services;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.tracker.Services;
using LogDB.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace com.logdb.windows.collector.Modules;

public sealed class WindowsMetricsCollectorModule : ExporterModuleBase
{
    private readonly ModuleHostFactory _moduleHostFactory;
    private readonly ILoggerFactory _loggerFactory;

    public WindowsMetricsCollectorModule(
        IOptionsMonitor<CollectorConfigDto> configMonitor,
        CollectorStatusRegistry statusRegistry,
        IRuntimeEndpointStore endpointStore,
        ModuleHostFactory moduleHostFactory,
        ILoggerFactory loggerFactory,
        ILogger<WindowsMetricsCollectorModule> logger)
        : base("Metrics", configMonitor, statusRegistry, endpointStore, logger)
    {
        _moduleHostFactory = moduleHostFactory;
        _loggerFactory = loggerFactory;
    }

    protected override bool IsEnabled(CollectorConfigDto config)
    {
        return config.Modules.Metrics.Enabled;
    }

    protected override object GetFingerprintModel(CollectorConfigDto config)
    {
        return new
        {
            config.LogDB,
            config.Server,
            config.Modules.Metrics
        };
    }

    protected override IHost BuildHost(CollectorConfigDto config, string endpoint)
    {
        var values = LegacyExporterConfigMapper.BuildMetricsConfig(config);

        // Diagnostic: log the effective Server:ServerName the metrics child host
        // will see. If config.Modules.Metrics.ServerNameOverride is set, the
        // mapper rewrote Server:ServerName to it — this confirms the override
        // actually made it through. If a user reports "Server name override
        // ignored", this log line plus the boot banner pinpoints whether the
        // override is in the loaded config or not.
        var bootLogger = _loggerFactory.CreateLogger<WindowsMetricsCollectorModule>();
        values.TryGetValue("Server:ServerName", out var effectiveServer);
        bootLogger.LogInformation(
            "Metrics module child host built | endpoint={Endpoint} | effective Server:ServerName={ServerName} | config.Modules.Metrics.ServerNameOverride={Override}",
            endpoint,
            string.IsNullOrWhiteSpace(effectiveServer) ? "(unset)" : effectiveServer,
            string.IsNullOrWhiteSpace(config.Modules.Metrics.ServerNameOverride) ? "(unset)" : config.Modules.Metrics.ServerNameOverride);

        var builder = _moduleHostFactory.CreateBuilder(values);

        // Ephemeral client: every LogAsync rebuilds the inner gRPC channel,
        // sends, flushes, disposes. Same pattern the UI Test uses — bypasses
        // the zombie-long-lived-channel class of bugs where a stale HTTP/2
        // connection silently swallows rows.
        builder.Services.AddSingleton<ILogDBClient>(_ =>
            new EphemeralLogDbClient(() => LogDbClientFactory.Create(config.LogDB, endpoint, _loggerFactory)));

        builder.Services.AddSingleton<WindowsMetricsReader>();
        builder.Services.AddHostedService<WindowsTrackerExportService>();

        return builder.Build();
    }
}
