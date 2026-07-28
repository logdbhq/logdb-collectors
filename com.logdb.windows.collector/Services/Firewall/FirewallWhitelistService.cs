using System.Net;

namespace com.logdb.windows.collector.Services.Firewall;

/// <summary>
/// Loads operator-edited "never block these IPs" entries from a text file.
/// Re-read on every sync cycle so changes land without bouncing the service.
/// Empty path / missing file = no whitelist, just returns an empty set.
/// </summary>
public sealed class FirewallWhitelistService
{
    private readonly ILogger<FirewallWhitelistService> _logger;

    public FirewallWhitelistService(ILogger<FirewallWhitelistService> logger)
    {
        _logger = logger;
    }

    public HashSet<string> Load(string? path)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(path)) return result;
        if (!File.Exists(path))
        {
            _logger.LogDebug("Whitelist file not found at {Path} — proceeding without whitelist", path);
            return result;
        }

        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;

                if (line.Contains('/'))
                {
                    var parts = line.Split('/');
                    if (parts.Length == 2 && IPAddress.TryParse(parts[0], out _) && int.TryParse(parts[1], out _))
                        result.Add(line);
                }
                else if (IPAddress.TryParse(line, out _))
                {
                    result.Add(line);
                }
            }

            _logger.LogInformation("Whitelist loaded: {Count} entries from {Path}", result.Count, path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read whitelist file at {Path} — proceeding without whitelist", path);
        }

        return result;
    }

    /// <summary>
    /// Removes every blocked entry that overlaps any whitelist entry, and
    /// returns how many were removed. Overlap — not just exact match — because
    /// feeds ship CIDRs (FireHOL netsets) while operators whitelist single IPs
    /// and vice versa. "Never block" wins over blocking: when a whitelisted IP
    /// falls inside a blocked CIDR, the whole CIDR is dropped, since Windows
    /// Firewall gives explicit Block rules priority over Allow rules and there
    /// is no way to carve an exception out of an applied block range.
    /// </summary>
    public static int Apply(HashSet<string> blocked, HashSet<string> whitelist)
    {
        if (whitelist.Count == 0 || blocked.Count == 0) return 0;

        var parsedWhitelist = whitelist
            .Select(entry => ParseEntry(entry))
            .Where(parsed => parsed is not null)
            .Select(parsed => parsed!.Value)
            .ToList();
        if (parsedWhitelist.Count == 0) return 0;

        var removed = 0;
        foreach (var entry in blocked.ToList())
        {
            var parsedBlocked = ParseEntry(entry);
            if (parsedBlocked is null) continue;

            foreach (var allow in parsedWhitelist)
            {
                if (Overlaps(parsedBlocked.Value, allow))
                {
                    blocked.Remove(entry);
                    removed++;
                    break;
                }
            }
        }

        return removed;
    }

    private static (byte[] Address, int PrefixLength)? ParseEntry(string entry)
    {
        var slash = entry.IndexOf('/');
        var addressPart = slash < 0 ? entry : entry[..slash];
        if (!IPAddress.TryParse(addressPart, out var address)) return null;

        var bytes = address.GetAddressBytes();
        var maxPrefix = bytes.Length * 8;
        if (slash < 0) return (bytes, maxPrefix);

        if (!int.TryParse(entry[(slash + 1)..], out var prefix) || prefix < 0 || prefix > maxPrefix)
            return null;
        return (bytes, prefix);
    }

    private static bool Overlaps((byte[] Address, int PrefixLength) a, (byte[] Address, int PrefixLength) b)
    {
        if (a.Address.Length != b.Address.Length) return false;

        var bits = Math.Min(a.PrefixLength, b.PrefixLength);
        var fullBytes = bits / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (a.Address[i] != b.Address[i]) return false;
        }

        var remainder = bits % 8;
        if (remainder == 0) return true;

        var mask = (byte)(0xFF << (8 - remainder));
        return (a.Address[fullBytes] & mask) == (b.Address[fullBytes] & mask);
    }
}
