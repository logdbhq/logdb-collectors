using System.Net;

namespace LogDB.Windows.Firewall.Services;

public class BlocklistFetcher
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BlocklistFetcher> _logger;

    public BlocklistFetcher(IHttpClientFactory httpClientFactory, ILogger<BlocklistFetcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Fetches and parses IPs/CIDRs from a blocklist URL.
    /// Supports plain IP lists, CIDR notation, and scored formats (IPsum).
    /// </summary>
    public async Task<HashSet<string>> FetchAsync(string sourceId, string url, int minScore = 0, CancellationToken ct = default)
    {
        var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            _logger.LogInformation("  Fetching {SourceId} from {Url}...", sourceId, url);

            var content = await client.GetStringAsync(url, ct);
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith("//"))
                    continue;

                var parts = trimmed.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    continue;

                var entry = parts[0];

                // Score-based filtering (e.g. IPsum: "IP\tSCORE")
                if (minScore > 0 && parts.Length >= 2)
                {
                    if (!int.TryParse(parts[1], out var score) || score < minScore)
                        continue;
                }

                // CIDR notation (e.g. 192.168.1.0/24)
                if (entry.Contains('/'))
                {
                    var cidrParts = entry.Split('/');
                    if (cidrParts.Length == 2 &&
                        IPAddress.TryParse(cidrParts[0], out _) &&
                        int.TryParse(cidrParts[1], out var prefix) &&
                        prefix >= 0 && prefix <= 128)
                    {
                        ips.Add(entry);
                    }
                }
                // Individual IP
                else if (IPAddress.TryParse(entry, out _))
                {
                    ips.Add(entry);
                }
            }

            _logger.LogInformation("  {SourceId}: loaded {Count} IPs/CIDRs", sourceId, ips.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "  Failed to fetch {SourceId} from {Url}", sourceId, url);
        }

        return ips;
    }
}
