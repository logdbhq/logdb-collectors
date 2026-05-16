using System.Text.Json;
using com.logdb.nginx.collector.Configuration;
using Microsoft.Extensions.Options;

namespace com.logdb.nginx.collector.Services;

public class FilterRuleService
{
    private readonly ILogger<FilterRuleService> _logger;
    private readonly NginxTargetOptions _targetOptions;
    private readonly string _filePath;
    private readonly object _lock = new();

    private HashSet<string> _excludePaths = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _excludeRemoteAddresses = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FilterRuleService(ILogger<FilterRuleService> logger, IOptions<NginxTargetOptions> targetOptions,
        IOptions<CheckpointOptions> checkpointOptions)
    {
        _logger = logger;
        _targetOptions = targetOptions.Value;

        var checkpointDir = Path.GetDirectoryName(Path.GetFullPath(checkpointOptions.Value.FilePath)) ?? ".";
        _filePath = Path.Combine(checkpointDir, "filter-rules.json");

        // Start with config defaults
        _excludePaths = new HashSet<string>(_targetOptions.ExcludePaths, StringComparer.OrdinalIgnoreCase);
        _excludeRemoteAddresses = new HashSet<string>(_targetOptions.ExcludeRemoteAddresses, StringComparer.OrdinalIgnoreCase);

        // Override with persisted state (if any)
        Load();
    }

    public bool ShouldExclude(string? path, string? remoteAddress)
    {
        lock (_lock)
        {
            if (path is not null && _excludePaths.Count > 0)
            {
                foreach (var prefix in _excludePaths)
                {
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            if (remoteAddress is not null && _excludeRemoteAddresses.Count > 0)
            {
                if (_excludeRemoteAddresses.Contains(remoteAddress))
                    return true;
            }
        }

        return false;
    }

    public FilterRulesDto GetRules()
    {
        lock (_lock)
        {
            return new FilterRulesDto
            {
                ExcludeStaticFiles = _targetOptions.ExcludeStaticFiles,
                ExcludePaths = _excludePaths.OrderBy(p => p).ToList(),
                ExcludeRemoteAddresses = _excludeRemoteAddresses.OrderBy(a => a).ToList()
            };
        }
    }

    public void AddExcludePath(string path)
    {
        lock (_lock)
        {
            _excludePaths.Add(path);
            Save();
            _logger.LogInformation("Added exclude path: {Path}", path);
        }
    }

    public void RemoveExcludePath(string path)
    {
        lock (_lock)
        {
            _excludePaths.Remove(path);
            Save();
            _logger.LogInformation("Removed exclude path: {Path}", path);
        }
    }

    public void AddExcludeRemoteAddress(string address)
    {
        lock (_lock)
        {
            _excludeRemoteAddresses.Add(address);
            Save();
            _logger.LogInformation("Added exclude remote address: {Address}", address);
        }
    }

    public void RemoveExcludeRemoteAddress(string address)
    {
        lock (_lock)
        {
            _excludeRemoteAddresses.Remove(address);
            Save();
            _logger.LogInformation("Removed exclude remote address: {Address}", address);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;

            var json = File.ReadAllText(_filePath);
            var state = JsonSerializer.Deserialize<PersistedFilterRules>(json, JsonOpts);
            if (state is null) return;

            if (state.ExcludePaths is not null)
                _excludePaths = new HashSet<string>(state.ExcludePaths, StringComparer.OrdinalIgnoreCase);
            if (state.ExcludeRemoteAddresses is not null)
                _excludeRemoteAddresses = new HashSet<string>(state.ExcludeRemoteAddresses, StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation("Loaded filter rules: {Paths} path exclusions, {IPs} IP exclusions",
                _excludePaths.Count, _excludeRemoteAddresses.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load filter rules from {Path}", _filePath);
        }
    }

    private void Save()
    {
        try
        {
            var state = new PersistedFilterRules
            {
                ExcludePaths = _excludePaths.OrderBy(p => p).ToList(),
                ExcludeRemoteAddresses = _excludeRemoteAddresses.OrderBy(a => a).ToList()
            };

            var json = JsonSerializer.Serialize(state, JsonOpts);
            var dir = Path.GetDirectoryName(_filePath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save filter rules to {Path}", _filePath);
        }
    }

    private class PersistedFilterRules
    {
        public List<string>? ExcludePaths { get; set; }
        public List<string>? ExcludeRemoteAddresses { get; set; }
    }
}

public class FilterRulesDto
{
    public bool ExcludeStaticFiles { get; set; }
    public List<string> ExcludePaths { get; set; } = new();
    public List<string> ExcludeRemoteAddresses { get; set; } = new();
}

public class FilterAddRequest
{
    public string Value { get; set; } = "";
}
