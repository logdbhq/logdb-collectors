using System.Net.Http.Json;
using System.Text.Json;
using com.logdb.windows.collector.Protos.Guard;
using com.logdb.windows.collector.shared.Contracts;
using Grpc.Core;
using Grpc.Net.Client;

namespace com.logdb.windows.collector.Services.Firewall;

/// <summary>
/// Subscribes to the LogDB Guard custom blocklist over gRPC and turns each
/// poll into a set of IPs the firewall engine can apply alongside the
/// public feeds. Empty / unreachable / unauthenticated = empty set, never
/// throws upward — the public-feed sync must still proceed.
///
/// Wire shape mirrors LogDB.Windows.Firewall/Services/CustomBlocklistClient.cs
/// so the unified collector and the standalone service are interchangeable
/// consumers of the same guard backend.
///
/// Endpoint resolution: explicit <see cref="CustomBlocklistConfigDto.GuardUrl"/>
/// wins; otherwise we hit the discovery service at
/// {DiscoveryUrl-host}/resolve/guard. Both paths produce a string usable
/// as <c>GrpcChannel.ForAddress</c> input (http://... or https://...).
/// </summary>
public sealed class GuardBlocklistClient : IDisposable
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GuardBlocklistClient> _logger;
    private GrpcChannel? _channel;
    private string? _cachedEndpoint;

    public GuardBlocklistClient(IHttpClientFactory httpClientFactory, ILogger<GuardBlocklistClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<HashSet<string>> FetchAsync(
        LogDbConfigDto logDbConfig,
        CustomBlocklistConfigDto guardConfig,
        CancellationToken cancellationToken = default)
    {
        var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!guardConfig.Enabled) return ips;
        if (string.IsNullOrWhiteSpace(logDbConfig.ApiKey))
        {
            _logger.LogWarning("Guard blocklist is enabled but LogDB:ApiKey is empty — skipping.");
            return ips;
        }

        var endpoint = await ResolveEndpointAsync(logDbConfig, guardConfig, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            _logger.LogWarning("Guard endpoint could not be resolved (GuardUrl empty and discovery failed).");
            return ips;
        }

        try
        {
            if (_channel is null || !string.Equals(_cachedEndpoint, endpoint, StringComparison.OrdinalIgnoreCase))
            {
                _channel?.Dispose();
                _channel = GrpcChannel.ForAddress(endpoint);
                _cachedEndpoint = endpoint;
            }

            var client = new GuardService.GuardServiceClient(_channel);
            var headers = new Metadata { { "authorization", $"Bearer {logDbConfig.ApiKey}" } };

            var response = await client.GetBlockedIpsAsync(
                new GetBlockedIpsRequest(),
                headers: headers,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var entry in response.BlockedIps)
            {
                if (!string.IsNullOrWhiteSpace(entry.IpAddress))
                    ips.Add(entry.IpAddress);
            }

            _logger.LogInformation("Guard blocklist: loaded {Count} IPs from {Endpoint}", ips.Count, endpoint);
        }
        catch (RpcException ex)
        {
            _logger.LogError("Guard blocklist gRPC error: Status={Status}, Detail={Detail}", ex.StatusCode, ex.Status.Detail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Guard blocklist fetch failed");
        }

        return ips;
    }

    private async Task<string?> ResolveEndpointAsync(
        LogDbConfigDto logDbConfig,
        CustomBlocklistConfigDto guardConfig,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(guardConfig.GuardUrl))
            return guardConfig.GuardUrl.Trim();

        var discoveryUrl = BuildGuardDiscoveryUrl(logDbConfig.DiscoveryUrl);
        if (string.IsNullOrWhiteSpace(discoveryUrl))
        {
            _logger.LogDebug("No DiscoveryUrl configured; cannot resolve guard endpoint automatically.");
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(GuardBlocklistClient));
            client.Timeout = TimeSpan.FromSeconds(5);
            using var request = new HttpRequestMessage(HttpMethod.Get, discoveryUrl);
            if (!string.IsNullOrWhiteSpace(logDbConfig.ApiKey))
                request.Headers.TryAddWithoutValidation("X-API-Key", logDbConfig.ApiKey);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
            if (payload.TryGetProperty("serviceUrl", out var prop))
            {
                var url = prop.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    _logger.LogInformation("Resolved guard endpoint via discovery: {Url}", url);
                    return url;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Guard discovery failed against {Url}", discoveryUrl);
        }

        return null;
    }

    /// <summary>
    /// Rewrites a /resolve/grpc-logger style discovery URL to its /resolve/guard
    /// sibling, so the operator doesn't need a second config knob. If the input
    /// doesn't look like a discovery URL we already understand, returns null
    /// rather than guess wrong.
    /// </summary>
    private static string? BuildGuardDiscoveryUrl(string? loggerDiscoveryUrl)
    {
        if (string.IsNullOrWhiteSpace(loggerDiscoveryUrl)) return null;
        const string resolveSegment = "/resolve/";
        var idx = loggerDiscoveryUrl.IndexOf(resolveSegment, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var prefix = loggerDiscoveryUrl[..(idx + resolveSegment.Length)];
        return prefix + "guard";
    }

    public void Dispose() => _channel?.Dispose();
}
