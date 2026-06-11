using com.logdb.windows.collector.Activity;
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
    private readonly ISendActivitySink _sendActivity;

    public HeartbeatCollectorModule(
        IOptionsMonitor<CollectorConfigDto> configMonitor,
        CollectorStatusRegistry statusRegistry,
        IRuntimeEndpointStore endpointStore,
        ModuleHostFactory moduleHostFactory,
        ILoggerFactory loggerFactory,
        ISendActivitySink sendActivity,
        ILogger<HeartbeatCollectorModule> logger)
        : base("Heartbeat", configMonitor, statusRegistry, endpointStore, logger)
    {
        _moduleHostFactory = moduleHostFactory;
        _loggerFactory = loggerFactory;
        _sendActivity = sendActivity;
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
        // HeartbeatBeatExportService actually reads).
        var bootLogger = _loggerFactory.CreateLogger<HeartbeatCollectorModule>();
        values.TryGetValue("Server:ServerName", out var effectiveServer);
        values.TryGetValue("Server:ServerEnvironment", out var effectiveEnv);
        bootLogger.LogInformation(
            "Heartbeat module child host built | endpoint={Endpoint} | effective Server:ServerName={ServerName} (= host tag on every beat) | effective Server:ServerEnvironment={Environment} | config.Modules.Heartbeat.ServerNameOverride={ServerOverride} | config.Modules.Heartbeat.EnvironmentOverride={EnvOverride}",
            endpoint,
            string.IsNullOrWhiteSpace(effectiveServer) ? "(unset)" : effectiveServer,
            string.IsNullOrWhiteSpace(effectiveEnv) ? "(unset)" : effectiveEnv,
            string.IsNullOrWhiteSpace(config.Modules.Heartbeat.ServerNameOverride) ? "(unset)" : config.Modules.Heartbeat.ServerNameOverride,
            string.IsNullOrWhiteSpace(config.Modules.Heartbeat.EnvironmentOverride) ? "(unset)" : config.Modules.Heartbeat.EnvironmentOverride);

        var builder = _moduleHostFactory.CreateBuilder(values);

        // Pin the SDK's wire-level DefaultApplication / DefaultEnvironment to the
        // same values HeartbeatBeatExportService writes per-record. LogBeat fields
        // are tag-wrappers and the SDK falls back to DefaultEnvironment (which
        // itself defaults to "production") whenever the per-record tag-wrapper is
        // serialized empty — without this pin the server-side log_beats.environment
        // column resolves to "production" instead of the effective ServerName /
        // EnvironmentOverride. Mirrors UiTestLogDispatcher.ResolveClientDefaults so
        // Test and real ingestion produce identical wire values.
        var defaultApplication = "LogDB Collector";
        var defaultEnvironment = effectiveEnv;

        // Ephemeral client: every LogBeatAsync rebuilds the inner gRPC channel,
        // sends, flushes, disposes. Same pattern the UI Test uses — bypasses
        // the zombie-long-lived-channel class of bugs where a stale HTTP/2
        // connection silently swallows rows.
        builder.Services.AddSingleton<ILogDBClient>(_ =>
            new RecordingLogDbClient(
                new EphemeralLogDbClient(() => LogDbClientFactory.Create(
                    config.LogDB,
                    endpoint,
                    _loggerFactory,
                    defaultApplication: defaultApplication,
                    defaultEnvironment: defaultEnvironment)),
                _sendActivity, "Heartbeat", Environment.MachineName));

        builder.Services.AddHostedService<HeartbeatBeatExportService>();

        return builder.Build();
    }
}
