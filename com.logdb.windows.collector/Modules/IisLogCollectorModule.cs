using com.logdb.windows.collector.Configuration;
using com.logdb.windows.collector.Health;
using com.logdb.windows.collector.Services;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.iis.Services;
using LogDB.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace com.logdb.windows.collector.Modules;

public sealed class IisLogCollectorModule : ExporterModuleBase
{
    private readonly ModuleHostFactory _moduleHostFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<IisLogCollectorModule> _logger;

    public IisLogCollectorModule(
        IOptionsMonitor<CollectorConfigDto> configMonitor,
        CollectorStatusRegistry statusRegistry,
        IRuntimeEndpointStore endpointStore,
        ModuleHostFactory moduleHostFactory,
        ILoggerFactory loggerFactory,
        ILogger<IisLogCollectorModule> logger)
        : base("IIS", configMonitor, statusRegistry, endpointStore, logger)
    {
        _moduleHostFactory = moduleHostFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    protected override bool IsEnabled(CollectorConfigDto config)
    {
        return config.Modules.IIS.Enabled && config.Modules.IIS.LogDirectories.Count > 0;
    }

    protected override object GetFingerprintModel(CollectorConfigDto config)
    {
        return new
        {
            config.LogDB,
            config.Server,
            config.Modules.IIS
        };
    }

    protected override void ApplyFlags(CollectorConfigDto config)
    {
        IISLogExportService.ResetState = config.Modules.IIS.ResetState;
        IISLogExportService.InitialStartDate = config.Modules.IIS.InitialStartDateUtc;

        if (!string.IsNullOrWhiteSpace(config.Modules.IIS.StateFilePath))
        {
            _logger.LogWarning(
                "IIS state file path override is configured but the reused IIS exporter stores state in its executable directory.");
        }
    }

    protected override IHost BuildHost(CollectorConfigDto config, string endpoint)
    {
        var values = LegacyExporterConfigMapper.BuildIisConfig(config);

        // Diagnostic: log the effective Server:ServerName the IIS child host
        // will see, plus the raw config.Modules.IIS.ServerNameOverride value.
        // If a user reports "Server name override ignored for IIS", this log
        // line plus the boot banner pinpoints whether the override is in the
        // loaded config or not.
        values.TryGetValue("Server:ServerName", out var effectiveServer);
        _logger.LogInformation(
            "IIS module child host built | endpoint={Endpoint} | effective Server:ServerName={ServerName} (= LogIISEvent.ServerName on every row) | config.Modules.IIS.ServerNameOverride={Override}",
            endpoint,
            string.IsNullOrWhiteSpace(effectiveServer) ? "(unset)" : effectiveServer,
            string.IsNullOrWhiteSpace(config.Modules.IIS.ServerNameOverride) ? "(unset)" : config.Modules.IIS.ServerNameOverride);

        var builder = _moduleHostFactory.CreateBuilder(values);

        // Ephemeral client: every LogAsync rebuilds the inner gRPC channel,
        // sends, flushes, disposes. Same pattern the UI Test uses — bypasses
        // the zombie-long-lived-channel class of bugs where a stale HTTP/2
        // connection silently swallows rows.
        builder.Services.AddSingleton<ILogDBClient>(_ =>
            new EphemeralLogDbClient(() => LogDbClientFactory.Create(config.LogDB, endpoint, _loggerFactory)));

        builder.Services.AddSingleton<IISLogReader>();
        builder.Services.AddSingleton<AzureAppServiceJsonReader>();
        builder.Services.AddSingleton<IISLogFilter>();
        builder.Services.AddSingleton<FileStateTracker>();
        builder.Services.AddHostedService<IISLogExportService>();

        return builder.Build();
    }
}
