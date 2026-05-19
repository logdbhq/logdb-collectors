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
        var builder = _moduleHostFactory.CreateBuilder(values);

        builder.Services.AddSingleton<ILogDBClient>(_ =>
            LogDbClientFactory.Create(config.LogDB, endpoint, _loggerFactory));

        builder.Services.AddSingleton<WindowsMetricsReader>();
        builder.Services.AddHostedService<WindowsTrackerExportService>();

        return builder.Build();
    }
}
