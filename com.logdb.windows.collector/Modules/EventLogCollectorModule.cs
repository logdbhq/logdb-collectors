using com.logdb.windows.collector.Configuration;
using com.logdb.windows.collector.Health;
using com.logdb.windows.collector.Hosting;
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
    private readonly CollectorRuntimeContext _runtimeContext;

    public EventLogCollectorModule(
        IOptionsMonitor<CollectorConfigDto> configMonitor,
        CollectorStatusRegistry statusRegistry,
        IRuntimeEndpointStore endpointStore,
        ModuleHostFactory moduleHostFactory,
        ILoggerFactory loggerFactory,
        CollectorRuntimeContext runtimeContext,
        ILogger<EventLogCollectorModule> logger)
        : base("EventLog", configMonitor, statusRegistry, endpointStore, logger)
    {
        _moduleHostFactory = moduleHostFactory;
        _loggerFactory = loggerFactory;
        _runtimeContext = runtimeContext;
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

        // Never harvest our own Windows event-log Source (the EventLog logging
        // provider writes under SourceName = ServiceName). Without this the
        // collector re-ingests its own status/warning lines from the Application
        // channel as data — a feedback loop that bloated windows_events.db.
        if (!string.IsNullOrWhiteSpace(_runtimeContext.ServiceName))
            values["EventViewer:SelfExcludeProviders:0"] = _runtimeContext.ServiceName;

        // Diagnostic: log the effective Server:ServerName (this is what
        // EventViewerExportService uses for the Computer field on every row)
        // plus both DTO override fields so an operator can verify at boot
        // which value reached the child host. ProviderNameOverride is included
        // because the mapper falls back to it for the server-name when the
        // dedicated ServerNameOverride field is empty.
        values.TryGetValue("Server:ServerName", out var effectiveServer);
        _logger.LogInformation(
            "EventLog module child host built | endpoint={Endpoint} | effective Server:ServerName={ServerName} (= LogWindowsEvent.Computer on every row) | config.Modules.EventLog.ServerNameOverride={ServerOverride} | config.Modules.EventLog.ProviderNameOverride={ProviderOverride}",
            endpoint,
            string.IsNullOrWhiteSpace(effectiveServer) ? "(unset)" : effectiveServer,
            string.IsNullOrWhiteSpace(config.Modules.EventLog.ServerNameOverride) ? "(unset)" : config.Modules.EventLog.ServerNameOverride,
            string.IsNullOrWhiteSpace(config.Modules.EventLog.ProviderNameOverride) ? "(unset)" : config.Modules.EventLog.ProviderNameOverride);

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
