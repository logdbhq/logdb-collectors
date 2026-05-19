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
        ILogDbServiceUrlResolver serviceUrlResolver,
        ModuleHostFactory moduleHostFactory,
        ILoggerFactory loggerFactory,
        ILogger<HeartbeatCollectorModule> logger)
        : base("Heartbeat", configMonitor, statusRegistry, serviceUrlResolver, logger)
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

        builder.Services.AddSingleton<ILogDBClient>(_ =>
            LogDbClientFactory.Create(config.LogDB, endpoint, _loggerFactory));

        builder.Services.AddHostedService<HeartbeatBeatExportService>();

        return builder.Build();
    }
}
