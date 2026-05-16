using System.Text.Json;
using com.logdb.nginx.collector.Configuration;
using Microsoft.Extensions.Options;

namespace com.logdb.nginx.collector.Services;

public class TargetToggleService
{
    private readonly ILogger<TargetToggleService> _logger;
    private readonly string _filePath;
    private readonly object _lock = new();

    // Override map: target name -> per-file enabled state
    // null means "use config default", true/false means override
    private readonly Dictionary<string, TargetOverride> _overrides = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public TargetToggleService(ILogger<TargetToggleService> logger, IOptions<CheckpointOptions> checkpointOptions)
    {
        _logger = logger;
        var checkpointDir = Path.GetDirectoryName(Path.GetFullPath(checkpointOptions.Value.FilePath)) ?? ".";
        _filePath = Path.Combine(checkpointDir, "target-toggles.json");
        _logger.LogInformation("Target toggle state file: {Path}", _filePath);
        Load();
    }

    public bool IsAccessLogEnabled(string targetName)
    {
        lock (_lock)
        {
            return _overrides.TryGetValue(targetName, out var o) ? o.AccessLogEnabled : true;
        }
    }

    public bool IsErrorLogEnabled(string targetName)
    {
        lock (_lock)
        {
            return _overrides.TryGetValue(targetName, out var o) ? o.ErrorLogEnabled : true;
        }
    }

    public void SetAccessLogEnabled(string targetName, bool enabled)
    {
        lock (_lock)
        {
            var o = GetOrCreateOverride(targetName);
            o.AccessLogEnabled = enabled;
            Save();
        }
    }

    public void SetErrorLogEnabled(string targetName, bool enabled)
    {
        lock (_lock)
        {
            var o = GetOrCreateOverride(targetName);
            o.ErrorLogEnabled = enabled;
            Save();
        }
    }

    public void SetTargetEnabled(string targetName, bool enabled)
    {
        lock (_lock)
        {
            var o = GetOrCreateOverride(targetName);
            o.AccessLogEnabled = enabled;
            o.ErrorLogEnabled = enabled;
            Save();
        }
    }

    public Dictionary<string, TargetOverride> GetAllOverrides()
    {
        lock (_lock)
        {
            return new Dictionary<string, TargetOverride>(_overrides, StringComparer.OrdinalIgnoreCase);
        }
    }

    private TargetOverride GetOrCreateOverride(string targetName)
    {
        if (!_overrides.TryGetValue(targetName, out var o))
        {
            o = new TargetOverride();
            _overrides[targetName] = o;
        }
        return o;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;

            var json = File.ReadAllText(_filePath);
            var state = JsonSerializer.Deserialize<Dictionary<string, TargetOverride>>(json, JsonOpts);
            if (state is null) return;

            _overrides.Clear();
            foreach (var (name, o) in state)
                _overrides[name] = o;

            _logger.LogInformation("Loaded target toggle state: {Count} overrides", _overrides.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load target toggle state from {Path}", _filePath);
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_overrides, JsonOpts);
            var dir = Path.GetDirectoryName(_filePath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save target toggle state to {Path}", _filePath);
        }
    }
}

public class TargetOverride
{
    public bool AccessLogEnabled { get; set; } = true;
    public bool ErrorLogEnabled { get; set; } = true;
}
