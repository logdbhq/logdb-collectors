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
