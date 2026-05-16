using LogDB.Windows.Firewall.Models;
using LogDB.Windows.Firewall.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "LogDB Windows Firewall";
});

var isConsoleMode = Environment.UserInteractive || args.Contains("--console") || args.Contains("-c");

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (isConsoleMode)
{
    Console.WriteLine("Running in CONSOLE MODE (for testing)");
    Console.WriteLine("  Press Ctrl+C to stop");
    Console.WriteLine("");
}

// Bind configuration
var firewallConfig = builder.Configuration.GetSection("Firewall").Get<FirewallConfig>() ?? new FirewallConfig();
var serverName = builder.Configuration["Server:ServerName"] ?? Environment.MachineName;

// Register services
builder.Services.AddHttpClient();
builder.Services.AddSingleton<BlocklistFetcher>();
builder.Services.AddSingleton<FirewallStateTracker>();
builder.Services.AddSingleton<SyncLogger>();
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<WhitelistService>>();
    var path = firewallConfig.WhitelistPath;
    if (string.IsNullOrWhiteSpace(path))
        path = Path.Combine(AppContext.BaseDirectory, "whitelist.txt");
    return new WhitelistService(logger, path);
});

builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<WindowsFirewallManager>>();
    return new WindowsFirewallManager(
        logger,
        firewallConfig.RulePrefix,
        firewallConfig.Direction,
        firewallConfig.DryRun,
        firewallConfig.MaxIpsPerRule);
});

// Register optional custom blocklist client
var apiKey = builder.Configuration["LogDB:ApiKey"];
var guardUrl = builder.Configuration["LogDB:GuardUrl"];
var customEnabled = firewallConfig.CustomBlocklist.Enabled;

if (customEnabled && !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(guardUrl))
{
    builder.Services.AddSingleton(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<CustomBlocklistClient>>();
        return new CustomBlocklistClient(logger, guardUrl, apiKey);
    });
}

builder.Services.AddHostedService<FirewallSyncService>();

try
{
    var host = builder.Build();

    // Startup banner
    Console.WriteLine("============================================================");
    Console.WriteLine("   LogDB Windows Firewall Service");
    Console.WriteLine("   Blocklist -> Windows Firewall Rules (auto-sync)");
    Console.WriteLine("============================================================");
    Console.WriteLine($"  Server:        {serverName}");
    Console.WriteLine($"  Sync Interval: {firewallConfig.SyncIntervalMinutes} min");
    Console.WriteLine($"  Rule Prefix:   {firewallConfig.RulePrefix}");
    Console.WriteLine($"  Direction:     {firewallConfig.Direction}");
    Console.WriteLine($"  Dry Run:       {firewallConfig.DryRun}");
    Console.WriteLine($"  Max IPs/Rule:  {firewallConfig.MaxIpsPerRule}");
    var whitelistDisplay = !string.IsNullOrWhiteSpace(firewallConfig.WhitelistPath)
        ? firewallConfig.WhitelistPath
        : Path.Combine(AppContext.BaseDirectory, "whitelist.txt");
    Console.WriteLine($"  Whitelist:     {whitelistDisplay}");
    Console.WriteLine("");
    Console.WriteLine("  Public Blocklists:");

    foreach (var (key, bl) in firewallConfig.PublicBlocklists)
    {
        var status = bl.Enabled ? "ON " : "OFF";
        var extra = bl.MinScore > 0 ? $" (score >= {bl.MinScore})" : "";
        Console.WriteLine($"    [{status}] {key}{extra}");
    }

    var customStatus = customEnabled && !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(guardUrl)
        ? "ON  (Guard connected)"
        : customEnabled
            ? "OFF (missing ApiKey or GuardUrl)"
            : "OFF";
    Console.WriteLine($"    [{customStatus}] Custom Blocklist");
    Console.WriteLine("============================================================");
    Console.WriteLine("");

    await host.RunAsync();
}
catch (Exception ex)
{
    Console.WriteLine("");
    Console.WriteLine($"FATAL ERROR: {ex.Message}");
    Console.WriteLine(ex.StackTrace);

    if (isConsoleMode)
    {
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }

    Environment.Exit(1);
}
