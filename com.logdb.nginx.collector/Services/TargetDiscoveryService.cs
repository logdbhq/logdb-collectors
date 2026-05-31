using System.IO.Enumeration;
using System.Text.Json;
using com.logdb.nginx.collector.Configuration;
using Microsoft.Extensions.Options;

namespace com.logdb.nginx.collector.Services;

/// <summary>
/// Discovers nginx log files by scanning a directory with glob patterns, and
/// owns the runtime-editable discovery settings (persisted to disk so changes
/// made in the UI survive restarts). Also exposes <see cref="StartAtEnd"/>,
/// consulted by the tailer when a file has no checkpoint yet.
/// </summary>
public class TargetDiscoveryService
{
    private readonly ILogger<TargetDiscoveryService> _logger;
    private readonly string _filePath;
    private readonly object _lock = new();
    private DiscoveryOptions _settings;

    // Short-lived cache of the directory scan so the hot tail path doesn't
    // enumerate the filesystem on every cycle.
    private List<DiscoveredFile>? _cache;
    private long _cacheStampMs;
    private static readonly long CacheTtlMs = 10_000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public TargetDiscoveryService(
        ILogger<TargetDiscoveryService> logger,
        IOptions<DiscoveryOptions> options,
        IOptions<CheckpointOptions> checkpointOptions)
    {
        _logger = logger;
        _settings = Clone(options.Value);

        var checkpointDir = Path.GetDirectoryName(Path.GetFullPath(checkpointOptions.Value.FilePath)) ?? ".";
        _filePath = Path.Combine(checkpointDir, "discovery-settings.json");
        Load();
    }

    public bool Enabled
    {
        get { lock (_lock) return _settings.Enabled; }
    }

    public bool StartAtEnd
    {
        get { lock (_lock) return _settings.StartAtEnd; }
    }

    public DiscoveryOptions GetSettings()
    {
        lock (_lock) return Clone(_settings);
    }

    public void UpdateSettings(DiscoveryOptions incoming)
    {
        lock (_lock)
        {
            _settings = Sanitize(incoming);
            _cache = null; // force a fresh scan on next read
            Save();
        }
        _logger.LogInformation(
            "Discovery settings updated: enabled={Enabled}, dir={Dir}, startAtEnd={StartAtEnd}, accessPatterns=[{Access}], errorPatterns=[{Error}]",
            _settings.Enabled, _settings.Directory, _settings.StartAtEnd,
            string.Join(", ", _settings.AccessLogPatterns), string.Join(", ", _settings.ErrorLogPatterns));
    }

    /// <summary>
    /// Files that currently match the patterns, regardless of the Enabled flag,
    /// so the UI can preview what would be watched before turning discovery on.
    /// </summary>
    public List<DiscoveredFile> Preview()
    {
        DiscoveryOptions s;
        lock (_lock) s = _settings;
        return Scan(s);
    }

    /// <summary>
    /// Discovered targets to merge with the explicit ones. Files already covered
    /// by an explicit target (matched on full path) are skipped to avoid
    /// double-tailing. Returns empty when discovery is disabled.
    /// </summary>
    public List<NginxTarget> DiscoverTargets(HashSet<string> trackedFullPaths)
    {
        DiscoveryOptions s;
        lock (_lock) s = _settings;
        if (!s.Enabled) return new List<NginxTarget>();

        var files = ScanCached(s);
        var result = new List<NginxTarget>(files.Count);
        foreach (var f in files)
        {
            if (trackedFullPaths.Contains(SafeFullPath(f.Path))) continue;

            result.Add(f.Type == "error"
                ? new NginxTarget { Name = f.Name, ErrorLogPath = f.Path, Enabled = true }
                : new NginxTarget { Name = f.Name, AccessLogPath = f.Path, Enabled = true });
        }
        return result;
    }

    private List<DiscoveredFile> ScanCached(DiscoveryOptions s)
    {
        lock (_lock)
        {
            if (_cache is not null && (Environment.TickCount64 - _cacheStampMs) < CacheTtlMs)
                return _cache;
        }

        var fresh = Scan(s);

        lock (_lock)
        {
            _cache = fresh;
            _cacheStampMs = Environment.TickCount64;
        }
        return fresh;
    }

    private List<DiscoveredFile> Scan(DiscoveryOptions s)
    {
        var result = new List<DiscoveredFile>();
        if (string.IsNullOrWhiteSpace(s.Directory) || !Directory.Exists(s.Directory))
            return result;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(s.Directory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discovery scan failed for {Dir}", s.Directory);
            return result;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (MatchesAny(name, s.ExcludePatterns)) continue;

            string? type = null;
            if (MatchesAny(name, s.AccessLogPatterns)) type = "access";
            else if (MatchesAny(name, s.ErrorLogPatterns)) type = "error";
            if (type is null) continue;

            result.Add(new DiscoveredFile
            {
                Name = name,
                Path = file,
                Type = type,
                Exists = true
            });
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    private static bool MatchesAny(string name, IEnumerable<string>? patterns)
    {
        if (patterns is null) return false;
        foreach (var p in patterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true))
                return true;
        }
        return false;
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private static DiscoveryOptions Sanitize(DiscoveryOptions s) => new()
    {
        Enabled = s.Enabled,
        Directory = string.IsNullOrWhiteSpace(s.Directory) ? "/var/log/nginx" : s.Directory.Trim(),
        AccessLogPatterns = CleanPatterns(s.AccessLogPatterns),
        ErrorLogPatterns = CleanPatterns(s.ErrorLogPatterns),
        ExcludePatterns = CleanPatterns(s.ExcludePatterns),
        StartAtEnd = s.StartAtEnd
    };

    private static List<string> CleanPatterns(List<string>? patterns) =>
        (patterns ?? new List<string>())
            .Select(p => p?.Trim() ?? "")
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static DiscoveryOptions Clone(DiscoveryOptions s) => new()
    {
        Enabled = s.Enabled,
        Directory = s.Directory,
        AccessLogPatterns = new List<string>(s.AccessLogPatterns ?? new()),
        ErrorLogPatterns = new List<string>(s.ErrorLogPatterns ?? new()),
        ExcludePatterns = new List<string>(s.ExcludePatterns ?? new()),
        StartAtEnd = s.StartAtEnd
    };

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var state = JsonSerializer.Deserialize<DiscoveryOptions>(json, JsonOpts);
            if (state is null) return;
            _settings = Sanitize(state);
            _logger.LogInformation("Loaded discovery settings from {Path}: enabled={Enabled}", _filePath, _settings.Enabled);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load discovery settings from {Path}", _filePath);
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_settings, JsonOpts));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save discovery settings to {Path}", _filePath);
        }
    }
}

public class DiscoveredFile
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "access";
    public bool Exists { get; set; }
}
