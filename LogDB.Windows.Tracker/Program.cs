using com.logdb.windows.tracker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LogDB.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

// Configure as Windows Service (only when running as service)
// When run from command line, it will run in console mode automatically
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "LogDB Windows Tracker";
});

// Always run in console mode when not installed as service
// Worker Services automatically detect console vs service mode
// Environment.UserInteractive is true when running from console, false when running as service
var isConsoleMode = Environment.UserInteractive || args.Contains("--console") || args.Contains("-c");

// Force console output
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.SetOut(new com.logdb.windows.tracker.TimestampedTextWriter(Console.Out));

if (isConsoleMode)
{
    Console.WriteLine("Running in console mode. Press Ctrl+C to stop.");
    Console.WriteLine("");
}

builder.Services.AddHttpClient();

var serviceUrl = await DiscoverServiceUrlAsync(builder.Configuration);
Console.WriteLine($"LogDB Service URL: {serviceUrl}");

// Register LogDBClient (uses gRPC protocol to send to grpc-logger, which forwards to Kafka)
builder.Services.AddSingleton<ILogDBClient>(sp =>
{
    var apiKey = builder.Configuration["LogDB:ApiKey"];
    
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        var errorMsg = "LogDB:ApiKey is not configured in appsettings.json. " +
                      "Please set your API key in the LogDB:ApiKey configuration section.";
        
        if (isConsoleMode)
        {
            Console.WriteLine("");
            Console.WriteLine("ERROR: " + errorMsg);
            Console.WriteLine("");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
        
        throw new InvalidOperationException(errorMsg);
    }

    var logDbOptions = new LogDBLoggerOptions
    {
        ApiKey = apiKey,
        ServiceUrl = serviceUrl, // Send to grpc-logger
        Protocol = LogDBProtocol.Native, // Use gRPC protocol (default)
        EnableBatching = false, // DISABLED for immediate delivery
        BatchSize = 1, // Not used when batching is disabled
        FlushInterval = TimeSpan.FromSeconds(1), // Not used when batching is disabled
        EnableCompression = builder.Configuration.GetValue<bool>("LogDB:EnableCompression", true),
        MaxRetries = builder.Configuration.GetValue<int>("LogDB:MaxRetries", 3),
        EnableCircuitBreaker = builder.Configuration.GetValue<bool>("LogDB:EnableCircuitBreaker", true)
    };
    
    Console.WriteLine("LogDB Client Configuration:");
    Console.WriteLine($"   Service URL: {serviceUrl}");
    Console.WriteLine($"   Protocol: {logDbOptions.Protocol}");
    Console.WriteLine($"   API Key: {(string.IsNullOrEmpty(logDbOptions.ApiKey) ? "NOT SET" : "CONFIGURED")}");
    Console.WriteLine($"   Batching: {logDbOptions.EnableBatching} (disabled - immediate delivery)");
    Console.WriteLine($"   Compression: {logDbOptions.EnableCompression}");
    
    var options = Options.Create(logDbOptions);
    var logger = sp.GetService<ILogger<LogDBClient>>();
    return new LogDBClient(options, logger);
});

static void ValidateHttpsUrl(string url, string source)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
    {
        throw new InvalidOperationException($"Invalid URL from {source}: {url}");
    }

    if (uri.Scheme != "https")
    {
        throw new InvalidOperationException($"HTTPS is required for security. Got '{uri.Scheme}://' from {source}. Please use https://");
    }
}

static async Task<string> DiscoverServiceUrlAsync(IConfiguration configuration)
{
    // Check if ServiceUrl is explicitly configured
    var configuredUrl = configuration["LogDB:ServiceUrl"];
    if (!string.IsNullOrWhiteSpace(configuredUrl) && !configuredUrl.Contains("your-service.com") && !configuredUrl.Contains("localhost"))
    {
        ValidateHttpsUrl(configuredUrl, "configuration");
        Console.WriteLine($"Using configured ServiceUrl: {configuredUrl}");
        return configuredUrl;
    }

    try
    {
        Console.WriteLine("Discovering LogDB service URL from discovery service...");
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
                ValidateHttpsUrl(discoveredUrl, "discovery service");
                Console.WriteLine($"Discovered LogDB service URL: {discoveredUrl}");
                return discoveredUrl;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Discovery service failed: {ex.Message}");
    }

    // No fallback - require explicit configuration or working discovery
    throw new InvalidOperationException(
        "Unable to discover LogDB service URL. " +
        "Please set LogDB:ServiceUrl in appsettings.json or ensure discovery.logdb.site is accessible.");
}

// Register services
builder.Services.AddSingleton<WindowsMetricsReader>();

// Register background service
builder.Services.AddHostedService<WindowsTrackerExportService>();

try
{
    var host = builder.Build();

    var serverName = builder.Configuration["Server:ServerName"] ?? Environment.MachineName;
    var serverEnvironment = builder.Configuration["Server:ServerEnvironment"] ?? "Production";
    var apiKey = builder.Configuration["LogDB:ApiKey"] ?? "NOT CONFIGURED";
    var collectionInterval = builder.Configuration.GetValue<int>("WindowsTracker:CollectionIntervalSeconds", 60);
    var targetCollection = builder.Configuration["WindowsTracker:Collection"] ?? "windows-metrics";

    Console.WriteLine("LogDB Windows Tracker Service");
    Console.WriteLine("CPU / Memory / Disk metrics to LogDB");
    Console.WriteLine("");
    Console.WriteLine($"Server Name: {serverName}");
    Console.WriteLine($"Environment: {serverEnvironment}");
    Console.WriteLine($"LogDB gRPC Service: {serviceUrl}");
    Console.WriteLine($"Collection: {targetCollection}");
    Console.WriteLine($"Interval: {collectionInterval} seconds");
    Console.WriteLine($"API Key: {(string.IsNullOrEmpty(apiKey) ? "NOT CONFIGURED" : "CONFIGURED")}");
    Console.WriteLine("");

    Console.WriteLine("Enabled Metrics:");
    Console.WriteLine($"  CPU:     {(builder.Configuration.GetValue<bool>("WindowsTracker:Metrics:CPU", true) ? "on" : "off")}");
    Console.WriteLine($"  Memory:  {(builder.Configuration.GetValue<bool>("WindowsTracker:Metrics:Memory", true) ? "on" : "off")}");
    Console.WriteLine($"  Disk:    {(builder.Configuration.GetValue<bool>("WindowsTracker:Metrics:Disk", true) ? "on" : "off")}");
    Console.WriteLine($"  Network: {(builder.Configuration.GetValue<bool>("WindowsTracker:Metrics:Network", false) ? "on" : "off")}");
    Console.WriteLine("");

    if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "NOT CONFIGURED")
    {
        Console.WriteLine("WARNING: LogDB:ApiKey is not configured!");
        Console.WriteLine("   Set your API key in appsettings.json.");
        Console.WriteLine("   The service will not send metrics without an API key.");
        Console.WriteLine("");
    }

    if (isConsoleMode)
    {
        Console.WriteLine("Starting Windows Tracker Service. Press Ctrl+C to stop.");
        Console.WriteLine("");
    }

    await host.RunAsync();
}
catch (Exception ex)
{
    Console.WriteLine("");
    Console.WriteLine("FATAL ERROR:");
    Console.WriteLine(ex.Message);
    Console.WriteLine("");
    Console.WriteLine("Stack Trace:");
    Console.WriteLine(ex.StackTrace);
    Console.WriteLine("");
    if (isConsoleMode)
    {
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }
    Environment.Exit(1);
}
