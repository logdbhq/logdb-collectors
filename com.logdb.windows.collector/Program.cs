using System.Diagnostics;
using com.logdb.windows.collector.Configuration;
using com.logdb.windows.collector.Control;
using com.logdb.windows.collector.Diagnostics;
using com.logdb.windows.collector.Health;
using com.logdb.windows.collector.Hosting;
using com.logdb.windows.collector.Modules;
using com.logdb.windows.collector.Services;
using com.logdb.windows.collector.Services.Firewall;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.shared.Services;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.EventLog;

var executablePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "com.logdb.windows.collector.exe";
var serviceCommandExitCode = await ServiceCommandRunner.ExecuteAsync(args, executablePath, Console.Out, CancellationToken.None);
if (serviceCommandExitCode >= 0)
{
    Environment.ExitCode = serviceCommandExitCode;
    return;
}

var runMode = ResolveMode(args);
var servicePipeName = ControlChannelDefaults.ResolvePipeName(CollectorInstanceMode.Service);
var consolePipeName = ControlChannelDefaults.ResolvePipeName(CollectorInstanceMode.Console);
var hasExplicitPipeOverride =
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ControlChannelDefaults.LegacyPipeEnvironmentVariable))
    || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ControlChannelDefaults.ConsolePipeEnvironmentVariable));

if (runMode == CollectorInstanceMode.Console && !hasExplicitPipeOverride)
{
    var serviceRunning = await IsServiceRunningAsync()
                         || await PipeProbe.IsReachableAsync(servicePipeName, TimeSpan.FromMilliseconds(400));

    if (serviceRunning)
    {
        Console.Error.WriteLine("LogDB Windows Collector service instance is already running. Stop the service before using --console.");
        Environment.ExitCode = 2;
        return;
    }
}

await CollectorConfigPersistence.EnsureExistsAsync();
Directory.CreateDirectory(CollectorPathDefaults.LogDirectory);

var lockName = "Global\\com.logdb.windows.collector.host";
using var instanceLock = SingleInstanceLock.Acquire(lockName);
if (!instanceLock.HasHandle)
{
    Console.Error.WriteLine("Another collector host instance is already running.");
    Environment.ExitCode = 3;
    return;
}

var runtimeContext = new CollectorRuntimeContext(
    runMode,
    runMode == CollectorInstanceMode.Service ? servicePipeName : consolePipeName,
    CollectorPathDefaults.ConfigPath,
    "LogDB Windows Collector");

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.Sources.Clear();
builder.Configuration
    .AddJsonFile(CollectorPathDefaults.ConfigPath, optional: false, reloadOnChange: true)
    .AddEnvironmentVariables(prefix: "LOGDB_COLLECTOR_");

builder.Services.Configure<CollectorConfigDto>(builder.Configuration);
builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

if (runMode == CollectorInstanceMode.Service)
{
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = runtimeContext.ServiceName;
    });
}

var logSink = new CollectorLogSink(CollectorPathDefaults.LogDirectory, capacity: 500);
builder.Services.AddSingleton(logSink);
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new CollectorLoggerProvider(logSink));
builder.Logging.AddEventLog(new EventLogSettings
{
    LogName = "Application",
    SourceName = runtimeContext.ServiceName,
    // Only Warning+ reaches the Windows Event Log. The EventLog module logs one
    // Information line per harvested event; routing those into the Application
    // log — which this collector also harvests — was an amplification loop that
    // grew the events DB to 21M+ rows in weeks. Operational detail still goes to
    // the collector's own file/UI sink (CollectorLoggerProvider) above.
    Filter = (category, level) => level >= LogLevel.Warning
});

builder.Services.AddSingleton(runtimeContext);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<CollectorStatusRegistry>(_ => new CollectorStatusRegistry(
    runtimeContext.ConfigPath,
    runtimeContext.Mode,
    runtimeContext.ControlPipeName,
    runtimeContext.ProcessId,
    runtimeContext.ServiceName,
    CollectorPathDefaults.FailureLogPath));
builder.Services.AddSingleton<ILogDbServiceUrlResolver, LogDbServiceUrlResolver>();
builder.Services.AddSingleton<IRuntimeEndpointStore, RuntimeEndpointStore>();
builder.Services.AddSingleton<ILogDbConnectionTester, LogDbConnectionTester>();
builder.Services.AddSingleton<ICollectorControlInspector, CollectorControlInspector>();
builder.Services.AddSingleton<PublicBlocklistFetcher>();
builder.Services.AddSingleton<FirewallWhitelistService>();
builder.Services.AddSingleton<GuardBlocklistClient>();
builder.Services.AddSingleton<FirewallSyncEngine>();
builder.Services.AddSingleton<IDiskSpooler, NullDiskSpooler>();
builder.Services.AddSingleton<ModuleHostFactory>();

builder.Services.AddHostedService<EventLogCollectorModule>();
builder.Services.AddHostedService<IisLogCollectorModule>();
builder.Services.AddHostedService<WindowsMetricsCollectorModule>();
builder.Services.AddHostedService<HeartbeatCollectorModule>();
builder.Services.AddHostedService<FirewallRuleModule>();
builder.Services.AddHostedService<RuntimeInfoPublisherService>();
builder.Services.AddHostedService<NamedPipeControlServer>();

var host = builder.Build();

// Boot banner — written through the CollectorLoggerProvider, so it lands in
// collector-YYYYMMDD.log the operator already reads. Independent of the .exe
// FileVersion (which was wrong for half this codebase's life) — proves at
// runtime what code is actually executing. Includes the in-source flags the
// operator most often asks about during a postmortem.
var bootLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("com.logdb.windows.collector.Boot");
var bootAssembly = System.Reflection.Assembly.GetExecutingAssembly();
var bootInformationalVersion = bootAssembly
    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
    .FirstOrDefault()?.InformationalVersion ?? "(no informational version)";
var bootAssemblyVersion = bootAssembly.GetName().Version?.ToString() ?? "(no assembly version)";
var bootExePath = Environment.ProcessPath ?? "(unknown)";
var bootDefaultCompression = new BatchOptionsDto().EnableCompression;  // reflects current DTO default
bootLogger.LogInformation(
    "▼ LogDB Windows Collector starting | mode={Mode} | assemblyVersion={AsmVer} | informationalVersion={InfoVer} | exePath={ExePath} | batchOptionsDto.EnableCompression(default)={CompressionDefault} | IRuntimeEndpointStore=registered | EphemeralLogDbClient=registered-in-module-BuildHost",
    runMode, bootAssemblyVersion, bootInformationalVersion, bootExePath, bootDefaultCompression);

// Per-module override values as actually loaded from appsettings.json. Lets the
// operator confirm at a glance whether their typed override survived the save
// path and reached the running service. If a value here is "(unset)" but the
// UI showed it filled in, the auto-save flow has a bug. If a value here is
// "windows.motivp.com" but real ingestion still uses D9501, the module isn't
// honoring the override at runtime.
var loadedConfig = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<CollectorConfigDto>>().CurrentValue;
bootLogger.LogInformation(
    "Loaded config overrides | Server.ServerName={GlobalServer} | EventLog.ProviderNameOverride={EventLogProvider} | EventLog.ServerNameOverride={EventLogServer} | IIS.ServerNameOverride={IisServer} | Metrics.ServerNameOverride={MetricsServer} | Heartbeat.ServerNameOverride={HeartbeatServer} | Heartbeat.EnvironmentOverride={HeartbeatEnv}",
    string.IsNullOrWhiteSpace(loadedConfig.Server.ServerName) ? "(unset)" : loadedConfig.Server.ServerName,
    string.IsNullOrWhiteSpace(loadedConfig.Modules.EventLog.ProviderNameOverride) ? "(unset)" : loadedConfig.Modules.EventLog.ProviderNameOverride,
    string.IsNullOrWhiteSpace(loadedConfig.Modules.EventLog.ServerNameOverride) ? "(unset)" : loadedConfig.Modules.EventLog.ServerNameOverride,
    string.IsNullOrWhiteSpace(loadedConfig.Modules.IIS.ServerNameOverride) ? "(unset)" : loadedConfig.Modules.IIS.ServerNameOverride,
    string.IsNullOrWhiteSpace(loadedConfig.Modules.Metrics.ServerNameOverride) ? "(unset)" : loadedConfig.Modules.Metrics.ServerNameOverride,
    string.IsNullOrWhiteSpace(loadedConfig.Modules.Heartbeat.ServerNameOverride) ? "(unset)" : loadedConfig.Modules.Heartbeat.ServerNameOverride,
    string.IsNullOrWhiteSpace(loadedConfig.Modules.Heartbeat.EnvironmentOverride) ? "(unset)" : loadedConfig.Modules.Heartbeat.EnvironmentOverride);

await host.RunAsync();

static CollectorInstanceMode ResolveMode(string[] args)
{
    if (args.Any(arg => string.Equals(arg, "--console", StringComparison.OrdinalIgnoreCase)))
    {
        return CollectorInstanceMode.Console;
    }

    return WindowsServiceHelpers.IsWindowsService()
        ? CollectorInstanceMode.Service
        : CollectorInstanceMode.Console;
}

static async Task<bool> IsServiceRunningAsync()
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "sc.exe",
        Arguments = "query \"LogDBWindowsCollector\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    try
    {
        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var output = $"{stdout}{Environment.NewLine}{stderr}";
        return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)
               || output.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase);
    }
    catch
    {
        return false;
    }
}
