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

    /// <summary>Operator-facing provenance of one Guard-blocked IP. Reason is
    /// free text written (or template-generated) in the desktop Guard app /
    /// FloodGuard console — unsanitized and unbounded server-side, so treat it
    /// as display/audit text only: never parse it, cap it before rendering.</summary>
    public sealed record GuardBlockedIpInfo(string Reason, string AddedBy);

    public async Task<Dictionary<string, GuardBlockedIpInfo>> FetchAsync(
        LogDbConfigDto logDbConfig,
        CustomBlocklistConfigDto guardConfig,
        CancellationToken cancellationToken = default)
    {
        var ips = new Dictionary<string, GuardBlockedIpInfo>(StringComparer.OrdinalIgnoreCase);
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
                    ips[entry.IpAddress] = new GuardBlockedIpInfo(entry.Reason ?? string.Empty, entry.AddedBy ?? string.Empty);
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

    /// <summary>
    /// Tells the Guard backend to drop the given IPs from its blocklist —
    /// called when an operator deletes a Guard-sourced rule locally, so the
    /// next sync doesn't just re-block them. Unlike <see cref="FetchAsync"/>
    /// this reports failure to the caller: the operator needs to know the
    /// backend still has the IPs.
    /// </summary>
    public async Task<(bool Success, string Message)> RemoveBlockedIpsAsync(
        LogDbConfigDto logDbConfig,
        CustomBlocklistConfigDto guardConfig,
        IReadOnlyCollection<string> ips,
        CancellationToken cancellationToken = default)
    {
        if (ips.Count == 0) return (true, "No IPs to remove.");
        if (string.IsNullOrWhiteSpace(logDbConfig.ApiKey))
            return (false, "LogDB:ApiKey is empty — cannot authenticate against the Guard backend.");

        var endpoint = await ResolveEndpointAsync(logDbConfig, guardConfig, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(endpoint))
            return (false, "Guard endpoint could not be resolved (GuardUrl empty and discovery failed).");

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

            var request = new RemoveBlockedIpsRequest();
            request.IpAddresses.AddRange(ips);

            var response = await client.RemoveBlockedIpsAsync(
                request,
                headers: headers,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Guard blocklist: removed {Count} IPs via {Endpoint}", response.RemovedCount, endpoint);
            return (true, $"Removed {response.RemovedCount} IP(s) from the Guard backend.");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            return (false, "Guard backend does not support IP removal yet (RemoveBlockedIps unimplemented).");
        }
        catch (RpcException ex)
        {
            _logger.LogError("Guard RemoveBlockedIps gRPC error: Status={Status}, Detail={Detail}", ex.StatusCode, ex.Status.Detail);
            return (false, $"Guard backend removal failed: {ex.StatusCode} {ex.Status.Detail}".Trim());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Guard RemoveBlockedIps failed");
            return (false, $"Guard backend removal failed: {ex.Message}");
        }
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
