using com.logdb.windows.collector.Configuration;
using com.logdb.windows.collector.Health;
using com.logdb.windows.collector.Services;
using com.logdb.windows.collector.shared.Contracts;
using LogDB.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace com.logdb.windows.collector.Modules;

public sealed class HeartbeatCollectorModule : ExporterModuleBase
{
    private readonly ModuleHostFactory _moduleHostFactory;
    private readonly ILoggerFactory _loggerFactory;

    public HeartbeatCollectorModule(
        IOptionsMonitor<CollectorConfigDto> configMonitor,
        CollectorStatusRegistry statusRegistry,
        IRuntimeEndpointStore endpointStore,
        ModuleHostFactory moduleHostFactory,
        ILoggerFactory loggerFactory,
        ILogger<HeartbeatCollectorModule> logger)
        : base("Heartbeat", configMonitor, statusRegistry, endpointStore, logger)
    {
        _moduleHostFactory = moduleHostFactory;
        _loggerFactory = loggerFactory;
    }

    protected override bool IsEnabled(CollectorConfigDto config)
    {
        return config.Modules.Heartbeat.Enabled;
    }

    protected override object GetFingerprintModel(CollectorConfigDto config)
    {
        return new
        {
            config.LogDB,
            config.Server,
            config.Modules.Heartbeat
        };
    }

    protected override IHost BuildHost(CollectorConfigDto config, string endpoint)
    {
        var values = LegacyExporterConfigMapper.BuildHeartbeatConfig(config);

        // Diagnostic: log the effective Server:ServerName + Server:ServerEnvironment
        // the heartbeat child host will see (these are the keys
        // HeartbeatBeatExportService actually reads). If the per-module overrides are
        // set on the DTO, the mapper rewrote these keys — this log line proves it
        // (or shows the bug if it didn't). The earlier diagnostic checked the wrong
        // key name (LogDB:DefaultEnvironment), which is never written by the mapper,
        // so the line always read "(unset)" even when the env was correctly configured.
        var bootLogger = _loggerFactory.CreateLogger<HeartbeatCollectorModule>();
        values.TryGetValue("Server:ServerName", out var effectiveServer);
        values.TryGetValue("Server:ServerEnvironment", out var effectiveEnv);
        values.TryGetValue("Server:ServerNameOverride", out var serverOverrideSignal);
        values.TryGetValue("Server:ServerEnvironmentOverride", out var envOverrideSignal);
        bootLogger.LogInformation(
            "Heartbeat module child host built | endpoint={Endpoint} | effective Server:ServerName={ServerName} | effective Server:ServerEnvironment={Environment} | Server:ServerNameOverride={ServerSignal} | Server:ServerEnvironmentOverride={EnvSignal} | config.Modules.Heartbeat.ServerNameOverride={ServerOverride} | config.Modules.Heartbeat.EnvironmentOverride={EnvOverride}",
            endpoint,
            string.IsNullOrWhiteSpace(effectiveServer) ? "(unset)" : effectiveServer,
            string.IsNullOrWhiteSpace(effectiveEnv) ? "(unset)" : effectiveEnv,
            string.IsNullOrWhiteSpace(serverOverrideSignal) ? "(unset)" : serverOverrideSignal,
            string.IsNullOrWhiteSpace(envOverrideSignal) ? "(unset)" : envOverrideSignal,
            string.IsNullOrWhiteSpace(config.Modules.Heartbeat.ServerNameOverride) ? "(unset)" : config.Modules.Heartbeat.ServerNameOverride,
            string.IsNullOrWhiteSpace(config.Modules.Heartbeat.EnvironmentOverride) ? "(unset)" : config.Modules.Heartbeat.EnvironmentOverride);

        var builder = _moduleHostFactory.CreateBuilder(values);

        // Ephemeral client: every LogBeatAsync rebuilds the inner gRPC channel,
        // sends, flushes, disposes. Same pattern the UI Test uses — bypasses
        // the zombie-long-lived-channel class of bugs where a stale HTTP/2
        // connection silently swallows rows.
        builder.Services.AddSingleton<ILogDBClient>(_ =>
            new EphemeralLogDbClient(() => LogDbClientFactory.Create(config.LogDB, endpoint, _loggerFactory)));

        builder.Services.AddHostedService<HeartbeatBeatExportService>();

        return builder.Build();
    }
}
