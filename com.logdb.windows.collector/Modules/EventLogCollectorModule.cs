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
    private readonly ILogger<EventLogCollectorModule> _logger;

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
        _logger = logger;
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

        // Diagnostic: log the effective Server:ServerName + override signal so
        // operators can verify at boot that the typed-override actually reached
        // the child host. If a user reports "Server name override ignored for
        // EventLog", this log line + the boot banner pinpoint whether the
        // override survived the save / reload path.
        values.TryGetValue("Server:ServerName", out var effectiveServer);
        values.TryGetValue("Server:ServerNameOverride", out var serverOverrideSignal);
        _logger.LogInformation(
            "EventLog module child host built | endpoint={Endpoint} | effective Server:ServerName={ServerName} | Server:ServerNameOverride={OverrideSignal} | config.Modules.EventLog.ServerNameOverride={Override}",
            endpoint,
            string.IsNullOrWhiteSpace(effectiveServer) ? "(unset)" : effectiveServer,
            string.IsNullOrWhiteSpace(serverOverrideSignal) ? "(unset)" : serverOverrideSignal,
            string.IsNullOrWhiteSpace(config.Modules.EventLog.ServerNameOverride) ? "(unset)" : config.Modules.EventLog.ServerNameOverride);

        var builder = _moduleHostFactory.CreateBuilder(values);

        // Ephemeral client: every LogAsync rebuilds the inner gRPC channel,
        // sends, flushes, disposes. Same pattern the UI Test uses — bypasses
        // the zombie-long-lived-channel class of bugs where a stale HTTP/2
        // connection silently swallows rows.
        builder.Services.AddSingleton<ILogDBClient>(_ =>
            new EphemeralLogDbClient(() => LogDbClientFactory.Create(config.LogDB, endpoint, _loggerFactory)));

        builder.Services.AddSingleton<EventLogReader>();
        builder.Services.AddSingleton<EventLogFilter>();
        builder.Services.AddSingleton<EventStateTracker>();
        builder.Services.AddHostedService<EventViewerExportService>();

        return builder.Build();
    }
}
