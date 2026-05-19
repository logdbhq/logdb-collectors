using System.Net.Http.Json;
using com.logdb.windows.collector.Health;
using com.logdb.windows.collector.Services;
using com.logdb.windows.collector.shared.Contracts;
using Microsoft.Extensions.Options;

namespace com.logdb.windows.collector.Modules;

public sealed class FirewallRuleModule : BackgroundService
{
    private readonly IOptionsMonitor<CollectorConfigDto> _configMonitor;
    private readonly CollectorStatusRegistry _statusRegistry;
    private readonly FirewallRuleApplier _firewallRuleApplier;
    private readonly IRuntimeEndpointStore _endpointStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FirewallRuleModule> _logger;

    public FirewallRuleModule(
        IOptionsMonitor<CollectorConfigDto> configMonitor,
        CollectorStatusRegistry statusRegistry,
        FirewallRuleApplier firewallRuleApplier,
        IRuntimeEndpointStore endpointStore,
        IHttpClientFactory httpClientFactory,
        ILogger<FirewallRuleModule> logger)
    {
        _configMonitor = configMonitor;
        _statusRegistry = statusRegistry;
        _firewallRuleApplier = firewallRuleApplier;
        _endpointStore = endpointStore;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const string moduleName = "Firewall";
        _statusRegistry.RegisterModule(moduleName);

        while (!stoppingToken.IsCancellationRequested)
        {
            var config = _configMonitor.CurrentValue;
            var enabled = config.Firewall.Enabled;
            _statusRegistry.SetEnabled(moduleName, enabled);

            if (!enabled)
            {
                _statusRegistry.MarkStopped(moduleName, "Disabled");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            try
            {
                var apiKey = config.LogDB.ApiKey;
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    _statusRegistry.MarkError(moduleName, "API key is required for firewall sync.");
                    await Task.Delay(TimeSpan.FromSeconds(config.Firewall.PollIntervalSeconds), stoppingToken);
                    continue;
                }

                var baseUrl = await _endpointStore.GetEndpointAsync(stoppingToken);
                var blockedIps = await FetchBlockedIpsAsync(baseUrl, apiKey, stoppingToken);

                var result = await _firewallRuleApplier.SyncAsync(config.Firewall, blockedIps, stoppingToken);
                if (result.Success)
                {
                    _statusRegistry.MarkRunning(moduleName);
                    _statusRegistry.MarkHeartbeat(moduleName);
                }
                else
                {
                    _statusRegistry.MarkError(moduleName, result.Message);
                }

                _logger.LogInformation("Firewall sync: {Message}", result.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _statusRegistry.MarkError(moduleName, ex.Message);
                _logger.LogError(ex, "Firewall module failed");
            }

            var interval = Math.Max(10, _configMonitor.CurrentValue.Firewall.PollIntervalSeconds);
            await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
        }
    }

    private async Task<List<BlockedIpEntry>> FetchBlockedIpsAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(FirewallRuleModule));
        client.Timeout = TimeSpan.FromSeconds(30);

        var apiUrl = BuildBlockedIpsUrl(baseUrl);
        var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.Add("X-API-Key", apiKey);

        var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var dtos = await response.Content.ReadFromJsonAsync<List<BlockedIpApiDto>>(cancellationToken)
                   ?? new List<BlockedIpApiDto>();

        return dtos
            .Where(dto => !string.IsNullOrWhiteSpace(dto.IpAddress))
            .Select(dto => new BlockedIpEntry(dto.IpAddress, dto.AddedBy, dto.AddedAt, dto.Reason))
            .ToList();
    }

    private static string BuildBlockedIpsUrl(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');

        if (trimmed.Contains("/api/", StringComparison.OrdinalIgnoreCase))
        {
            var apiIndex = trimmed.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
            trimmed = trimmed[..apiIndex];
        }

        return $"{trimmed}/api/guard/blocked-ips";
    }

    private sealed class BlockedIpApiDto
    {
        public string IpAddress { get; set; } = "";
        public string AddedBy { get; set; } = "";
        public DateTime AddedAt { get; set; }
        public string? Reason { get; set; }
    }
}
