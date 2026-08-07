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
/// <summary>Per-IP provenance the Guard backend supplies and public feeds
/// cannot: why it was blocked, by whom, and the real block time.</summary>
public sealed record BlockedIpProvenance(string Reason, string AddedBy, DateTime? AddedAtUtc);

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

    /// <summary>
    /// Aligns the index with the active per-feed IP sets of a completed sync
    /// cycle. Returns (added, removed) for logging.
    ///
    /// <paramref name="retainedSources"/> names feeds whose fetch failed this
    /// cycle: their rules were left untouched in the firewall, so their index
    /// entries must survive too. Without this the index would report those IPs
    /// as no longer blocked while the OS is still blocking them — the index
    /// would be lying in the safe direction, which is the worse one, because
    /// the operator would go re-block IPs that are already blocked.
    ///
    /// A source that is in neither collection was deliberately disabled or
    /// removed, and its entries are pruned as before.
    /// </summary>
    public (int Added, int Removed) Reconcile(
        IReadOnlyDictionary<string, HashSet<string>> activeSetsBySource,
        IReadOnlySet<string> retainedSources,
        DateTime nowUtc,
        IReadOnlyDictionary<string, BlockedIpProvenance>? provenance = null)
    {
        lock (_gate)
        {
            try
            {
                var entries = LoadLocked();
                var added = 0;
                var changed = false;

                var activeIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (source, ips) in activeSetsBySource)
                {
                    foreach (var ip in ips)
                    {
                        activeIps.Add(ip);

                        BlockedIpProvenance? known = null;
                        if (provenance != null && provenance.TryGetValue(ip, out var found)) known = found;

                        if (!entries.TryGetValue(ip, out var entry))
                        {
                            entries[ip] = NewEntry(ip, source, nowUtc, known);
                            added++;
                            continue;
                        }

                        // Refresh provenance on every cycle, so an index written
                        // before these fields existed — or an entry whose reason
                        // was edited in Guard — heals instead of staying blank
                        // forever.
                        changed |= ApplyProvenance(entry, known);
                    }
                }

                var stale = entries
                    .Where(kvp => !activeIps.Contains(kvp.Key) && !retainedSources.Contains(kvp.Value.Source))
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var ip in stale)
                {
                    entries.Remove(ip);
                }

                if (added > 0 || stale.Count > 0 || changed)
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
                    // Reason and added-by are searchable too: "why is this
                    // blocked" and "what else did I block for brute force" are
                    // the same question from opposite ends.
                    matched = matched.Where(e =>
                        e.Ip.Contains(trimmedFilter, StringComparison.OrdinalIgnoreCase) ||
                        e.Source.Contains(trimmedFilter, StringComparison.OrdinalIgnoreCase) ||
                        e.Reason.Contains(trimmedFilter, StringComparison.OrdinalIgnoreCase) ||
                        e.AddedBy.Contains(trimmedFilter, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// Builds a fresh entry, preferring the Guard backend's own block time over
    /// "now". Falling back to the cycle time is exact only for a genuinely new
    /// block; for anything else it is a first-observed time, and the entry says
    /// so via <see cref="BlockedIpEntryDto.BlockedAtApproximate"/> so the UI
    /// never presents it as fact.
    /// </summary>
    private static BlockedIpEntryDto NewEntry(
        string ip,
        string source,
        DateTime nowUtc,
        BlockedIpProvenance? known)
    {
        var entry = new BlockedIpEntryDto
        {
            Ip = ip,
            Source = source,
            BlockedAtUtc = known?.AddedAtUtc ?? nowUtc,
            BlockedAtApproximate = known?.AddedAtUtc is null
        };
        ApplyProvenance(entry, known);
        return entry;
    }

    /// <summary>
    /// Copies reason / added-by / real block time onto an entry. Returns whether
    /// anything actually changed, so a steady state doesn't rewrite the file
    /// every poll. Never downgrades: absent provenance leaves what is already
    /// stored alone, because a Guard fetch that failed this cycle must not blank
    /// out the reasons recorded last cycle.
    /// </summary>
    private static bool ApplyProvenance(BlockedIpEntryDto entry, BlockedIpProvenance? known)
    {
        if (known is null) return false;

        var changed = false;

        var reason = CompactText(known.Reason, 200);
        if (reason.Length > 0 && !string.Equals(entry.Reason, reason, StringComparison.Ordinal))
        {
            entry.Reason = reason;
            changed = true;
        }

        var addedBy = CompactText(known.AddedBy, 80);
        if (addedBy.Length > 0 && !string.Equals(entry.AddedBy, addedBy, StringComparison.Ordinal))
        {
            entry.AddedBy = addedBy;
            changed = true;
        }

        // An authoritative timestamp always wins over a first-observed guess.
        if (known.AddedAtUtc is { } exact && (entry.BlockedAtApproximate || entry.BlockedAtUtc != exact))
        {
            entry.BlockedAtUtc = exact;
            entry.BlockedAtApproximate = false;
            changed = true;
        }

        return changed;
    }

    /// <summary>The reason is unsanitized and unbounded server-side; collapse
    /// whitespace and cap it before it reaches the index or any UI.</summary>
    private static string CompactText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var compact = System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim();
        return compact.Length <= maxLength ? compact : compact[..maxLength] + "…";
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
