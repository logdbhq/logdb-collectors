using System.Net;

namespace com.logdb.windows.collector.Services.Firewall;

/// <summary>
/// Pulls a public IP-reputation feed (FireHOL, Tor exit list, IPsum, etc.)
/// and parses out IPv4/IPv6 addresses and CIDR ranges. Tolerant of the format
/// variations these feeds use: comment lines, score-suffixed lines, mixed
/// separators. Failures don't throw — return an empty set and let the caller
/// proceed; we never want one slow/down feed to take the whole sync with it.
///
/// Ported from <c>LogDB.Windows.Firewall.Services.BlocklistFetcher</c> so the
/// unified collector has the same source-of-blocked-IPs semantics as the
/// standalone firewall service, instead of the broken HTTP-against-gRPC path
/// that <see cref="Modules.FirewallRuleModule"/> had through 1.2.x.
/// </summary>
public sealed class PublicBlocklistFetcher
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PublicBlocklistFetcher> _logger;

    public PublicBlocklistFetcher(IHttpClientFactory httpClientFactory, ILogger<PublicBlocklistFetcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<HashSet<string>> FetchAsync(
        string feedId,
        string url,
        int minScore,
        CancellationToken cancellationToken = default)
    {
        var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var client = _httpClientFactory.CreateClient(nameof(PublicBlocklistFetcher));
            client.Timeout = TimeSpan.FromSeconds(30);

            _logger.LogInformation("Fetching blocklist {FeedId} from {Url}", feedId, url);
            var content = await client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var raw in lines)
            {
                var trimmed = raw.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith("//"))
                    continue;

                var parts = trimmed.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                var entry = parts[0];

                // IPsum-style score column.
                if (minScore > 0 && parts.Length >= 2)
                {
                    if (!int.TryParse(parts[1], out var score) || score < minScore)
                        continue;
                }

                if (entry.Contains('/'))
                {
                    var cidr = entry.Split('/');
                    if (cidr.Length == 2 &&
                        IPAddress.TryParse(cidr[0], out _) &&
                        int.TryParse(cidr[1], out var prefix) &&
                        prefix >= 0 && prefix <= 128)
                    {
                        ips.Add(entry);
                    }
                }
                else if (IPAddress.TryParse(entry, out _))
                {
                    ips.Add(entry);
                }
            }

            _logger.LogInformation("Blocklist {FeedId}: parsed {Count} entries", feedId, ips.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch blocklist {FeedId} from {Url}", feedId, url);
        }

        return ips;
    }
}
