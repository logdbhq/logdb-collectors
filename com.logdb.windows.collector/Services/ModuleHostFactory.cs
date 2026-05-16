using com.logdb.windows.collector.Diagnostics;

namespace com.logdb.windows.collector.Services;

public sealed class ModuleHostFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public ModuleHostFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public HostApplicationBuilder CreateBuilder(IDictionary<string, string?> configuration)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true
        });

        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ForwardingLoggerProvider(_loggerFactory));
        builder.Services.Configure<HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
        });

        return builder;
    }
}
