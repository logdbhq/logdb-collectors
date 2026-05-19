using com.logdb.windows.collector.Configuration;
using com.logdb.windows.collector.Health;
using com.logdb.windows.collector.Services;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.eventviewer.Services;
using LogDB.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace com.logdb.windows.collector.Modules;

public sealed class EventLogCollectorModule : ExporterModuleBase
{
    private readonly ModuleHostFactory _moduleHostFactory;
    private readonly ILoggerFactory _loggerFactory;

    public EventLogCollectorModule(
        IOptionsMonitor<CollectorConfigDto> configMonitor,
        CollectorStatusRegistry statusRegistry,
        IRuntimeEndpointStore endpointStore,
        ModuleHostFactory moduleHostFactory,
        ILoggerFactory loggerFactory,
        ILogger<EventLogCollectorModule> logger)
        : base("EventLog", configMonitor, statusRegistry, endpointStore, logger)
    {
        _moduleHostFactory = moduleHostFactory;
        _loggerFactory = loggerFactory;
    }

    protected override bool IsEnabled(CollectorConfigDto config)
    {
        return config.Modules.EventLog.Enabled;
    }

    protected override object GetFingerprintModel(CollectorConfigDto config)
    {
        return new
        {
            config.LogDB,
            config.Server,
            config.Modules.EventLog
        };
    }

    protected override void ApplyFlags(CollectorConfigDto config)
    {
        EventViewerExportService.ResetState = config.Modules.EventLog.ResetState;
        EventViewerExportService.InitialStartDate = config.Modules.EventLog.InitialStartDateUtc;
    }

    protected override IHost BuildHost(CollectorConfigDto config, string endpoint)
    {
        var values = LegacyExporterConfigMapper.BuildEventLogConfig(config);
        var builder = _moduleHostFactory.CreateBuilder(values);

        builder.Services.AddSingleton<ILogDBClient>(_ =>
            LogDbClientFactory.Create(config.LogDB, endpoint, _loggerFactory));

        builder.Services.AddSingleton<EventLogReader>();
        builder.Services.AddSingleton<EventLogFilter>();
        builder.Services.AddSingleton<EventStateTracker>();
        builder.Services.AddHostedService<EventViewerExportService>();

        return builder.Build();
    }
}
