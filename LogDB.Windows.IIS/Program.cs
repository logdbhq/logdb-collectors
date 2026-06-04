using com.logdb.windows.iis.Models;
using com.logdb.windows.iis.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LogDB.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

// Configure as Windows Service (only when running as service)
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "LogDB IIS Log Exporter";
});

// Configure logging
builder.Logging.AddFilter<LogDBLoggerProvider>("com.logdb.windows.iis", LogLevel.Warning);

var isConsoleMode = Environment.UserInteractive || args.Contains("--console") || args.Contains("-c");
var resetState = args.Contains("--reset");

// Parse initial start date argument (yyyy-MM-dd format)
var dateArg = args.FirstOrDefault(a => !a.StartsWith("-") && DateTime.TryParseExact(a, "yyyy-MM-dd",
    System.Globalization.CultureInfo.InvariantCulture,
    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
    out _));

if (!string.IsNullOrEmpty(dateArg) && DateTime.TryParseExact(dateArg, "yyyy-MM-dd",
    System.Globalization.CultureInfo.InvariantCulture,
    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
    out var initialDate))
{
    IISLogExportService.InitialStartDate = initialDate;
    Console.WriteLine($"Initial start date set: {initialDate:yyyy-MM-dd} (will filter out entries before this date)");
}

if (resetState)
{
    IISLogExportService.ResetState = true;
    Console.WriteLine("--reset flag set: will clear saved state and start fresh");
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.SetOut(new com.logdb.windows.iis.TimestampedTextWriter(Console.Out));

if (isConsoleMode)
{
    Console.WriteLine("Running in CONSOLE MODE (for testing)");
    Console.WriteLine("   Press Ctrl+C to stop");
    Console.WriteLine("");
}

// Discover LogDB service URL (grpc-logger)
var serviceUrl = await DiscoverServiceUrlAsync(builder.Configuration);

// Register LogDBClient (gRPC)
builder.Services.AddSingleton<ILogDBClient>(sp =>
{
    var apiKey = builder.Configuration["LogDB:ApiKey"];

    if (string.IsNullOrWhiteSpace(apiKey))
    {
        var errorMsg = "LogDB:ApiKey is not configured in appsettings.json.";

        if (isConsoleMode)
        {
            Console.WriteLine("");
            Console.WriteLine("ERROR: " + errorMsg);
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        throw new InvalidOperationException(errorMsg);
    }

    var logDbOptions = new LogDBLoggerOptions
    {
        ApiKey = apiKey,
        ServiceUrl = serviceUrl,
        Protocol = LogDBProtocol.Native,
        EnableBatching = builder.Configuration.GetValue<bool>("LogDB:EnableBatching", false),
        BatchSize = builder.Configuration.GetValue<int>("LogDB:BatchSize", 100),
        FlushInterval = TimeSpan.FromSeconds(builder.Configuration.GetValue<int>("LogDB:FlushIntervalSeconds", 5)),
        EnableCompression = builder.Configuration.GetValue<bool>("LogDB:EnableCompression", true),
        MaxRetries = builder.Configuration.GetValue<int>("LogDB:MaxRetries", 3),
        EnableCircuitBreaker = builder.Configuration.GetValue<bool>("LogDB:EnableCircuitBreaker", true)
    };

    var options = Options.Create(logDbOptions);
    var logger = sp.GetService<ILogger<LogDBClient>>();
    return new LogDBClient(options, logger);
});

static async Task<string> DiscoverServiceUrlAsync(IConfiguration configuration)
{
    var configuredUrl = configuration["LogDB:ServiceUrl"];
    if (!string.IsNullOrWhiteSpace(configuredUrl) && !configuredUrl.Contains("your-service.com") && !configuredUrl.Contains("localhost"))
    {
        return configuredUrl;
    }

    try
    {
        Console.WriteLine("Discovering LogDB service URL...");
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(5);

        var apiKey = configuration["LogDB:ApiKey"];
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://discovery.logdb.site/resolve/grpc-logger");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
        }

        using var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        if (json.TryGetProperty("serviceUrl", out var serviceUrlProp))
        {
            var discoveredUrl = serviceUrlProp.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(discoveredUrl))
            {
                Console.WriteLine($"LogDB Service URL: {discoveredUrl}");
                return discoveredUrl;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Discovery service failed: {ex.Message}");
    }

    throw new InvalidOperationException(
        "Unable to discover LogDB service URL. " +
        "Please set LogDB:ServiceUrl in appsettings.json or ensure discovery.logdb.site is accessible.");
}

// Register services
builder.Services.AddSingleton<IISLogReader>();
builder.Services.AddSingleton<AzureAppServiceJsonReader>();
builder.Services.AddSingleton<IISLogFilter>();
builder.Services.AddSingleton<FileStateTracker>(sp =>
    new FileStateTracker(sp.GetService<ILogger<FileStateTracker>>()));

// Register background service
builder.Services.AddHostedService<IISLogExportService>();

try
{
    var host = builder.Build();

    var serverName = builder.Configuration["Server:ServerName"] ?? Environment.MachineName;
    var serverEnvironment = builder.Configuration["Server:ServerEnvironment"] ?? "Production";
    var apiKey = builder.Configuration["LogDB:ApiKey"] ?? "NOT CONFIGURED";

    // Build effective sources for display
    var displayConfig = new IISExportConfig();
    builder.Configuration.GetSection("IIS").Bind(displayConfig);
    var effectiveSources = displayConfig.GetEffectiveSources();

    Console.WriteLine("");
    Console.WriteLine("LogDB IIS Log Exporter");
    Console.WriteLine("");
    Console.WriteLine($"  Server:      {serverName}");
    Console.WriteLine($"  Environment: {serverEnvironment}");
    Console.WriteLine($"  gRPC:        {serviceUrl}");
    Console.WriteLine($"  API Key:     {(string.IsNullOrEmpty(apiKey) || apiKey == "NOT CONFIGURED" ? "NOT CONFIGURED" : "CONFIGURED")}");

    if (effectiveSources.Count > 0)
    {
        Console.WriteLine($"  Log Sources: {effectiveSources.Count}");
        foreach (var source in effectiveSources)
        {
            Console.WriteLine($"    [{source.Format}] {source.Path}");
        }
    }
    else
    {
        Console.WriteLine("  Log Sources: NONE CONFIGURED");
    }

    Console.WriteLine("");

    if (effectiveSources.Count == 0)
    {
        Console.WriteLine("");
        Console.WriteLine("  WARNING: No IIS log sources configured in appsettings.json!");
        Console.WriteLine("  Add LogSources or LogPaths to appsettings.json.");
        Console.WriteLine("");
    }

    if (isConsoleMode)
    {
        Console.WriteLine("");
        Console.WriteLine("Starting IIS Log Export Service...");
        Console.WriteLine("");
    }

    await host.RunAsync();
}
catch (Exception ex)
{
    Console.WriteLine("");
    Console.WriteLine("FATAL ERROR:");
    Console.WriteLine(ex.Message);
    Console.WriteLine(ex.StackTrace);
    Console.WriteLine("");
    if (isConsoleMode)
    {
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }
    Environment.Exit(1);
}
