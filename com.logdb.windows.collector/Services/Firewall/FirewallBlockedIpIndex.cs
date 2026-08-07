using System.Text.Json;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.shared.Services;

namespace com.logdb.windows.collector.Services.Firewall;

/// <summary>
/// Persistent per-IP block index: ip → (source feed, first-blocked timestamp).
/// Windows Firewall stores no per-address dates and history entries cap their
/// IP samples, so this index is the only complete answer to "which IPs are
/// blocked and since when". Reconciled against the full active IP sets after
/// every successful (non-dry-run) sync: new IPs are stamped with the cycle
/// time — exact for genuinely new blocks, first-observed for IPs that predate
/// the index — and IPs no longer in any rule are dropped. Stored as one JSON
/// array in %ProgramData%\LogDB\collector\firewall-blocked-ips.json; failures
/// are logged and swallowed, never allowed to break a sync.
/// </summary>
public sealed class FirewallBlockedIpIndex
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private readonly ILogger<FirewallBlockedIpIndex> _logger;
    private readonly object _gate = new();
    private Dictionary<string, BlockedIpEntryDto>? _entries;

    public FirewallBlockedIpIndex(ILogger<FirewallBlockedIpIndex> logger)
        : this(CollectorPathDefaults.FirewallBlockedIpIndexPath, logger)
    {
    }

    public FirewallBlockedIpIndex(string path, ILogger<FirewallBlockedIpIndex> logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>Aligns the index with the active per-feed IP sets of a completed
    /// sync cycle. Returns (added, removed) for logging.</summary>
    public (int Added, int Removed) Reconcile(
        IReadOnlyDictionary<string, HashSet<string>> activeSetsBySource,
        DateTime nowUtc)
    {
        lock (_gate)
        {
            try
            {
                var entries = LoadLocked();
                var added = 0;

                var activeIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (source, ips) in activeSetsBySource)
                {
                    foreach (var ip in ips)
                    {
                        activeIps.Add(ip);
                        if (!entries.ContainsKey(ip))
                        {
                            entries[ip] = new BlockedIpEntryDto { Ip = ip, Source = source, BlockedAtUtc = nowUtc };
                            added++;
                        }
                    }
                }

                var stale = entries.Keys.Where(ip => !activeIps.Contains(ip)).ToList();
                foreach (var ip in stale)
                {
                    entries.Remove(ip);
                }

                if (added > 0 || stale.Count > 0)
                {
                    SaveLocked(entries);
                }

                return (added, stale.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Blocked-IP index reconcile failed — index left unchanged");
                return (0, 0);
            }
        }
    }

    /// <summary>
    /// Filters by kind (public feeds vs the Guard subscription), then by exact
    /// source, then by free text — all on this side, so Matched stays a true
    /// count even when the returned list is capped at <paramref name="max"/>.
    /// <paramref name="guardDisplayName"/> is what separates "public" from
    /// "guard"; it comes from config, which the index itself doesn't read.
    /// </summary>
    public BlockedIpListResponseDto Query(
        string? filter,
        int max,
        string? source = null,
        string? kind = null,
        string? guardDisplayName = null)
    {
        max = Math.Clamp(max, 1, 2000);
        lock (_gate)
        {
            try
            {
                var entries = LoadLocked();
                var guardName = string.IsNullOrWhiteSpace(guardDisplayName) ? "LogDB Guard" : guardDisplayName.Trim();

                // Built from every entry, not the filtered subset, so narrowing
                // the view never empties the picker you narrowed it with.
                var sources = entries.Values
                    .GroupBy(e => e.Source, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new BlockedIpSourceDto
                    {
                        Source = g.Key,
                        Count = g.Count(),
                        IsGuard = string.Equals(g.Key, guardName, StringComparison.OrdinalIgnoreCase)
                    })
                    .OrderBy(s => s.IsGuard)
                    .ThenBy(s => s.Source, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                IEnumerable<BlockedIpEntryDto> matched = entries.Values;

                switch (kind)
                {
                    case BlockedIpKinds.Guard:
                        matched = matched.Where(e => string.Equals(e.Source, guardName, StringComparison.OrdinalIgnoreCase));
                        break;
                    case BlockedIpKinds.Public:
                        matched = matched.Where(e => !string.Equals(e.Source, guardName, StringComparison.OrdinalIgnoreCase));
                        break;
                }

                if (!string.IsNullOrWhiteSpace(source))
                {
                    var wanted = source.Trim();
                    matched = matched.Where(e => string.Equals(e.Source, wanted, StringComparison.OrdinalIgnoreCase));
                }

                var trimmedFilter = filter?.Trim() ?? string.Empty;
                if (trimmedFilter.Length > 0)
                {
                    matched = matched.Where(e =>
                        e.Ip.Contains(trimmedFilter, StringComparison.OrdinalIgnoreCase) ||
                        e.Source.Contains(trimmedFilter, StringComparison.OrdinalIgnoreCase));
                }

                var matchedList = matched.ToList();

                return new BlockedIpListResponseDto
                {
                    Total = entries.Count,
                    Matched = matchedList.Count,
                    Sources = sources,
                    Entries = matchedList
                        .OrderByDescending(e => e.BlockedAtUtc)
                        .ThenBy(e => e.Ip, StringComparer.OrdinalIgnoreCase)
                        .Take(max)
                        .ToList()
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Blocked-IP index query failed");
                return new BlockedIpListResponseDto();
            }
        }
    }

    private Dictionary<string, BlockedIpEntryDto> LoadLocked()
    {
        if (_entries != null)
        {
            return _entries;
        }

        var entries = new Dictionary<string, BlockedIpEntryDto>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(_path))
        {
            var loaded = JsonSerializer.Deserialize<List<BlockedIpEntryDto>>(File.ReadAllText(_path), JsonOptions);
            foreach (var entry in loaded ?? new List<BlockedIpEntryDto>())
            {
                if (!string.IsNullOrWhiteSpace(entry.Ip))
                {
                    entries[entry.Ip] = entry;
                }
            }
        }

        _entries = entries;
        return entries;
    }

    private void SaveLocked(Dictionary<string, BlockedIpEntryDto> entries)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(entries.Values, JsonOptions));
        File.Move(tempPath, _path, overwrite: true);
    }
}
