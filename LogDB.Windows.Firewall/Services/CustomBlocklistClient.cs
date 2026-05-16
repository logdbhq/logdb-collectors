using Grpc.Core;
using Grpc.Net.Client;
using LogDB.Windows.Firewall.Protos;

namespace LogDB.Windows.Firewall.Services;

public class CustomBlocklistClient : IDisposable
{
    private readonly ILogger<CustomBlocklistClient> _logger;
    private readonly string _guardUrl;
    private readonly string _apiKey;
    private GrpcChannel? _channel;

    public CustomBlocklistClient(ILogger<CustomBlocklistClient> logger, string guardUrl, string apiKey)
    {
        _logger = logger;
        _guardUrl = guardUrl;
        _apiKey = apiKey;
    }

    /// <summary>
    /// Fetches custom blocked IPs from LogDB Guard via gRPC.
    /// Returns empty set on failure (non-fatal).
    /// </summary>
    public async Task<HashSet<string>> FetchBlockedIpsAsync(CancellationToken ct = default)
    {
        var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            _channel ??= GrpcChannel.ForAddress(_guardUrl);
            var client = new GuardService.GuardServiceClient(_channel);

            var headers = new Metadata
            {
                { "authorization", $"Bearer {_apiKey}" }
            };

            var response = await client.GetBlockedIpsAsync(
                new GetBlockedIpsRequest(),
                headers: headers,
                cancellationToken: ct);

            foreach (var blocked in response.BlockedIps)
            {
                if (!string.IsNullOrWhiteSpace(blocked.IpAddress))
                    ips.Add(blocked.IpAddress);
            }

            _logger.LogInformation("  Custom blocklist: loaded {Count} IPs from Guard", ips.Count);
        }
        catch (RpcException ex)
        {
            _logger.LogError("  Custom blocklist gRPC error: Status={Status}, Detail={Detail}", ex.StatusCode, ex.Status.Detail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "  Custom blocklist fetch failed");
        }

        return ips;
    }

    public void Dispose()
    {
        _channel?.Dispose();
    }
}
