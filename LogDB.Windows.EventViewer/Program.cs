using com.logdb.windows.eventviewer.Services;
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
    options.ServiceName = "LogDB Event Viewer Exporter";
});

// Configure logging
builder.Logging.AddFilter<LogDBLoggerProvider>("com.logdb.windows.eventviewer", LogLevel.Warning);

var isConsoleMode = Environment.UserInteractive || args.Contains("--console") || args.Contains("-c");
var resetState = args.Contains("--reset");
var showStatus = args.Contains("--status") || args.Contains("-s");
var showCount = args.Contains("--count");

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
    EventViewerExportService.InitialStartDate = initialDate;
    Console.WriteLine($"Initial start date set: {initialDate:yyyy-MM-dd} (will fetch events from this date on first run)");
}

if (resetState)
{
    EventViewerExportService.ResetState = true;
    Console.WriteLine("--reset flag set: will clear saved state and start fresh");
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.SetOut(new com.logdb.windows.eventviewer.TimestampedTextWriter(Console.Out));

if (isConsoleMode)
{
    Console.WriteLine("Running in CONSOLE MODE (for testing)");
    Console.WriteLine("   Press Ctrl+C to stop");
    Console.WriteLine("");
}

// Handle --status and --count (display local state, no export)
if (showStatus || showCount)
{
    HandleStatusOrCount(showStatus, showCount);
    return;
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

static void HandleStatusOrCount(bool showStatus, bool showCount)
{
    Console.WriteLine("");
    Console.WriteLine("LogDB Event Viewer Exporter - Status");
    Console.WriteLine("");

    var stateTracker = new EventStateTracker();

    // Read all sources from the local state file
    var stateFilePath = Path.Combine(AppContext.BaseDirectory, "eventviewer-state.json");
    if (!File.Exists(stateFilePath))
    {
        Console.WriteLine("  No export state found. The exporter has not run yet or state was reset.");
        Console.WriteLine("");
        return;
    }

    try
    {
        var json = File.ReadAllText(stateFilePath);
        var stateData = System.Text.Json.JsonSerializer.Deserialize<EventStateData>(json);

        if (stateData == null || stateData.Sources.Count == 0)
        {
            Console.WriteLine("  No export state found. The exporter has not run yet or state was reset.");
            Console.WriteLine("");
            return;
        }

        long grandTotal = 0;

        foreach (var (logSource, state) in stateData.Sources.OrderBy(s => s.Key))
        {
            Console.WriteLine($"  Log Source: {logSource}");

            if (showStatus)
            {
                var localTime = state.LastTimestamp.ToLocalTime();
                Console.WriteLine($"     Last Exported:  {localTime:yyyy-MM-dd HH:mm:ss} (local)");
                Console.WriteLine($"                     {state.LastTimestamp:yyyy-MM-dd HH:mm:ss} (UTC)");
                Console.WriteLine($"     Last Event ID:  {state.LastEventId}");
                Console.WriteLine($"     Last Export At: {state.LastExportAt:yyyy-MM-dd HH:mm:ss} UTC");
            }

            if (showCount)
            {
                Console.WriteLine($"     Total Exported: {state.TotalExported:N0} events");
                grandTotal += state.TotalExported;
            }

            Console.WriteLine("");
        }

        if (showCount)
        {
            Console.WriteLine($"  Grand Total: {grandTotal:N0} events");
            Console.WriteLine("");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Error reading state: {ex.Message}");
        Console.WriteLine("");
    }

    Console.WriteLine("");
}

// Register services
builder.Services.AddSingleton<EventLogReader>();
builder.Services.AddSingleton<EventLogFilter>();
builder.Services.AddSingleton<EventStateTracker>(sp =>
    new EventStateTracker(sp.GetService<ILogger<EventStateTracker>>()));

// Register background service
builder.Services.AddHostedService<EventViewerExportService>();

try
{
    var host = builder.Build();

    var serverName = builder.Configuration["Server:ServerName"] ?? Environment.MachineName;
    var serverEnvironment = builder.Configuration["Server:ServerEnvironment"] ?? "Production";
    var apiKey = builder.Configuration["LogDB:ApiKey"] ?? "NOT CONFIGURED";
    var logSources = builder.Configuration.GetSection("EventViewer:LogSources").Get<List<string>>() ?? new();
    var eventLevels = builder.Configuration.GetSection("EventViewer:EventLevels").Get<List<string>>() ?? new();

    Console.WriteLine("");
    Console.WriteLine("LogDB Event Viewer Exporter");
    Console.WriteLine("");
    Console.WriteLine($"  Server:      {serverName}");
    Console.WriteLine($"  Environment: {serverEnvironment}");
    Console.WriteLine($"  gRPC:        {serviceUrl}");
    Console.WriteLine($"  API Key:     {(string.IsNullOrEmpty(apiKey) || apiKey == "NOT CONFIGURED" ? "NOT CONFIGURED" : "CONFIGURED")}");
    Console.WriteLine($"  Log Sources: {(logSources.Count > 0 ? string.Join(", ", logSources) : "NONE CONFIGURED")}");
    Console.WriteLine($"  Levels:      {(eventLevels.Count > 0 ? string.Join(", ", eventLevels) : "NONE CONFIGURED")}");
    Console.WriteLine("");

    if (logSources.Count == 0)
    {
        Console.WriteLine("");
        Console.WriteLine("  WARNING: No EventViewer:LogSources configured in appsettings.json!");
        Console.WriteLine("  Add log sources like: [\"System\", \"Application\", \"Security\"]");
        Console.WriteLine("");
    }

    if (isConsoleMode)
    {
        Console.WriteLine("");
        Console.WriteLine("Starting Event Viewer Export Service...");
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
