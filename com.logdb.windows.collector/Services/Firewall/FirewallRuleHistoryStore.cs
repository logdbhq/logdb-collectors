using System.Text.Json;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.shared.Services;

namespace com.logdb.windows.collector.Services.Firewall;

/// <summary>
/// Append-only record of every firewall change the collector makes, one JSON
/// object per line in %ProgramData%\LogDB\collector\firewall-history.jsonl.
/// JSONL so a crash mid-write corrupts at most the last line (skipped on
/// read), and so appends never rewrite what admins may be tailing.
///
/// Recording must never break a sync cycle: all IO failures are logged and
/// swallowed. The file is trimmed back to <see cref="MaxEntries"/> once it
/// grows past that by a margin, so trims are amortized rather than per-append.
/// </summary>
public sealed class FirewallRuleHistoryStore
{
    private const int MaxEntries = 1000;
    private const int TrimThreshold = 1200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private readonly ILogger<FirewallRuleHistoryStore> _logger;
    private readonly object _gate = new();
    private int _appendsSinceTrimCheck;

    public FirewallRuleHistoryStore(ILogger<FirewallRuleHistoryStore> logger)
        : this(CollectorPathDefaults.FirewallHistoryPath, logger)
    {
    }

    public FirewallRuleHistoryStore(string path, ILogger<FirewallRuleHistoryStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public void Record(FirewallRuleHistoryEntryDto entry)
    {
        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                // A crash mid-append can leave a torn line with no trailing
                // newline; appending straight onto it would corrupt THIS entry
                // too. Heal by terminating the torn line first.
                var payload = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
                if (!FileEndsWithNewline()) payload = Environment.NewLine + payload;
                File.AppendAllText(_path, payload);

                if (++_appendsSinceTrimCheck >= TrimThreshold - MaxEntries)
                {
                    _appendsSinceTrimCheck = 0;
                    TrimLocked();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record firewall history entry to {Path}", _path);
            }
        }
    }

    public IReadOnlyList<FirewallRuleHistoryEntryDto> GetRecent(int max)
    {
        lock (_gate)
        {
            var entries = ReadAllLocked();
            entries.Reverse();
            if (entries.Count > max) entries.RemoveRange(max, entries.Count - max);
            return entries;
        }
    }

    private bool FileEndsWithNewline()
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length == 0) return true;
            stream.Seek(-1, SeekOrigin.End);
            return stream.ReadByte() == '\n';
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
    }

    private List<FirewallRuleHistoryEntryDto> ReadAllLocked()
    {
        var entries = new List<FirewallRuleHistoryEntryDto>();
        try
        {
            if (!File.Exists(_path)) return entries;
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<FirewallRuleHistoryEntryDto>(line, JsonOptions);
                    if (entry != null) entries.Add(entry);
                }
                catch (JsonException)
                {
                    // Torn line from a crash mid-append — skip it.
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read firewall history from {Path}", _path);
        }

        return entries;
    }

    private void TrimLocked()
    {
        var entries = ReadAllLocked();
        if (entries.Count <= TrimThreshold) return;

        var keep = entries.Skip(entries.Count - MaxEntries);
        var tempPath = _path + ".tmp";
        File.WriteAllLines(tempPath, keep.Select(entry => JsonSerializer.Serialize(entry, JsonOptions)));
        File.Move(tempPath, _path, overwrite: true);
    }
}
