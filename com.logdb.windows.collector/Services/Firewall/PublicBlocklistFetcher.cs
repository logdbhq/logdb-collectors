using System.Net;

namespace com.logdb.windows.collector.Services.Firewall;

/// <summary>Outcome of one feed fetch. <see cref="Success"/> false means we
/// never saw the feed's contents (HTTP error, timeout, DNS failure) — it does
/// NOT mean the feed is empty, and the caller must not treat the empty
/// <see cref="Ips"/> set as "nothing to block". A feed that genuinely returned
/// zero parseable entries is Success=true with an empty set.</summary>
public sealed record BlocklistFetchResult(bool Success, HashSet<string> Ips, string Error)
{
    public static BlocklistFetchResult Ok(HashSet<string> ips) => new(true, ips, string.Empty);

    public static BlocklistFetchResult Failed(string error) =>
        new(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase), error);
}

/// <summary>
/// Pulls a public IP-reputation feed (FireHOL, Tor exit list, IPsum, etc.)
/// and parses out IPv4/IPv6 addresses and CIDR ranges. Tolerant of the format
/// variations these feeds use: comment lines, score-suffixed lines, mixed
/// separators. Failures don't throw — they come back as
/// <see cref="BlocklistFetchResult.Success"/> false so one slow/down feed
/// never takes the whole sync with it.
///
/// Through 1.5.3 this returned a bare set, so a fetch failure was
/// indistinguishable from an empty feed and the sync engine responded by
/// deleting that feed's rules — a transient upstream outage silently unblocked
/// every IP the feed had contributed. The result type exists to make that
/// distinction impossible to drop on the floor.
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

    public async Task<BlocklistFetchResult> FetchAsync(
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Service shutdown — not a feed failure, and must not be reported as one.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch blocklist {FeedId} from {Url}", feedId, url);
            return BlocklistFetchResult.Failed(ex.Message);
        }

        return BlocklistFetchResult.Ok(ips);
    }
}
