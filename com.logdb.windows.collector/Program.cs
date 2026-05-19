using System.Diagnostics;
using com.logdb.windows.collector.Configuration;
using com.logdb.windows.collector.Control;
using com.logdb.windows.collector.Diagnostics;
using com.logdb.windows.collector.Health;
using com.logdb.windows.collector.Hosting;
using com.logdb.windows.collector.Modules;
using com.logdb.windows.collector.Services;
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
    SourceName = runtimeContext.ServiceName
});

builder.Services.AddSingleton(runtimeContext);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<CollectorStatusRegistry>(_ => new CollectorStatusRegistry(
    runtimeContext.ConfigPath,
    runtimeContext.Mode,
    runtimeContext.ControlPipeName,
    runtimeContext.ProcessId,
    runtimeContext.ServiceName));
builder.Services.AddSingleton<ILogDbServiceUrlResolver, LogDbServiceUrlResolver>();
builder.Services.AddSingleton<IRuntimeEndpointStore, RuntimeEndpointStore>();
builder.Services.AddSingleton<ILogDbConnectionTester, LogDbConnectionTester>();
builder.Services.AddSingleton<ICollectorControlInspector, CollectorControlInspector>();
builder.Services.AddSingleton<FirewallRuleApplier>();
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
