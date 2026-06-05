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

    public static int Apply(HashSet<string> blocked, HashSet<string> whitelist)
    {
        if (whitelist.Count == 0 || blocked.Count == 0) return 0;
        var removed = 0;
        foreach (var entry in whitelist)
        {
            if (blocked.Remove(entry)) removed++;
        }
        return removed;
    }
}
