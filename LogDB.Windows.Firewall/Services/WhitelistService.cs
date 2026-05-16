using System.Net;

namespace LogDB.Windows.Firewall.Services;

/// <summary>
/// Loads whitelisted IPs/CIDRs from a text file.
/// The file is re-read on every sync cycle so edits take effect without a restart.
/// </summary>
public class WhitelistService
{
    private readonly ILogger<WhitelistService> _logger;
    private readonly string _path;

    public WhitelistService(ILogger<WhitelistService> logger, string path)
    {
        _logger = logger;
        _path = path;
    }

    /// <summary>
    /// Reads the whitelist file and returns the set of whitelisted IPs/CIDRs.
    /// Returns empty set if path is empty or file doesn't exist.
    /// </summary>
    public HashSet<string> Load()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(_path))
            return result;

        if (!File.Exists(_path))
        {
            _logger.LogDebug("Whitelist file not found: {Path}", _path);
            return result;
        }

        try
        {
            var lines = File.ReadAllLines(_path);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                    continue;

                // Validate: plain IP or CIDR
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

            _logger.LogInformation("Loaded {Count} entries from whitelist: {Path}", result.Count, _path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read whitelist file: {Path}", _path);
        }

        return result;
    }

    /// <summary>
    /// Removes whitelisted IPs from a source set. Returns the number of IPs removed.
    /// </summary>
    public int Apply(HashSet<string> ips, HashSet<string> whitelist)
    {
        if (whitelist.Count == 0)
            return 0;

        var removed = 0;
        foreach (var entry in whitelist)
        {
            if (ips.Remove(entry))
                removed++;
        }

        return removed;
    }
}
