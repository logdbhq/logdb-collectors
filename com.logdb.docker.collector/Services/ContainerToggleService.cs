using System.Text.Json;
using com.logdb.docker.collector.Configuration;
using Microsoft.Extensions.Options;

namespace com.logdb.docker.collector.Services;

public enum LogMode
{
    All,
    ErrorsOnly
}

public class ContainerToggleService
{
    private readonly ILogger<ContainerToggleService> _logger;
    private readonly string _filePath;
    private readonly object _lock = new();

    // Opt-in model: only containers in this set are enabled
    private readonly HashSet<string> _enabledContainers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LogMode> _logModes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _startDates = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _globalStartDate;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ContainerToggleService(ILogger<ContainerToggleService> logger, IOptions<CheckpointOptions> checkpointOptions)
    {
        _logger = logger;
        // Save next to checkpoints (on the Docker volume)
        var checkpointDir = Path.GetDirectoryName(Path.GetFullPath(checkpointOptions.Value.FilePath)) ?? ".";
        _filePath = Path.Combine(checkpointDir, "container-toggles.json");
        _logger.LogInformation("Toggle state file: {Path}", _filePath);
        Load();
    }

    public bool IsDisabled(string containerId)
    {
        lock (_lock)
        {
            return !_enabledContainers.Contains(containerId);
        }
    }

    public bool SetEnabled(string containerId, bool enabled)
    {
        lock (_lock)
        {
            var changed = enabled
                ? _enabledContainers.Add(containerId)
                : _enabledContainers.Remove(containerId);
            Save();
            return changed;
        }
    }

    public IReadOnlySet<string> GetEnabledIds()
    {
        lock (_lock)
        {
            return new HashSet<string>(_enabledContainers, StringComparer.OrdinalIgnoreCase);
        }
    }

    public LogMode GetLogMode(string containerId)
    {
        lock (_lock)
        {
            return _logModes.TryGetValue(containerId, out var mode) ? mode : LogMode.All;
        }
    }

    public void SetAllEnabled(IEnumerable<string> containerIds, bool enabled)
    {
        lock (_lock)
        {
            if (enabled)
            {
                foreach (var id in containerIds)
                    _enabledContainers.Add(id);
            }
            else
            {
                _enabledContainers.Clear();
            }
            Save();
        }
    }

    public void SetAllLogModes(IEnumerable<string> containerIds, LogMode mode)
    {
        lock (_lock)
        {
            foreach (var id in containerIds)
                _logModes[id] = mode;
            Save();
        }
    }

    public LogMode ToggleLogMode(string containerId)
    {
        lock (_lock)
        {
            var current = _logModes.TryGetValue(containerId, out var mode) ? mode : LogMode.All;
            var next = current == LogMode.All ? LogMode.ErrorsOnly : LogMode.All;
            _logModes[containerId] = next;
            Save();
            return next;
        }
    }

    public void SetGlobalStartDate(DateTime? date)
    {
        lock (_lock)
        {
            _globalStartDate = date;
            Save();
        }
    }

    public DateTime? GetGlobalStartDate()
    {
        lock (_lock) { return _globalStartDate; }
    }

    public void SetContainerStartDate(string containerId, DateTime? date)
    {
        lock (_lock)
        {
            if (date is null)
                _startDates.Remove(containerId);
            else
                _startDates[containerId] = date.Value;
            Save();
        }
    }

    public DateTime? GetContainerStartDate(string containerId)
    {
        lock (_lock)
        {
            if (_startDates.TryGetValue(containerId, out var date))
                return date;
            return _globalStartDate;
        }
    }

    public Dictionary<string, DateTime?> GetAllStartDates()
    {
        lock (_lock)
        {
            var result = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, date) in _startDates)
                result[id] = date;
            return result;
        }
    }

    public bool ShouldExport(string containerId, string stream, string? parsedLevel = null)
    {
        lock (_lock)
        {
            if (!_enabledContainers.Contains(containerId))
                return false;

            if (!_logModes.TryGetValue(containerId, out var mode) || mode == LogMode.All)
                return true;

            // ErrorsOnly: allow stderr stream OR .NET parsed levels Error/Critical/Warning
            if (stream.Equals("stderr", StringComparison.OrdinalIgnoreCase))
                return true;

            if (parsedLevel is not null)
            {
                return parsedLevel is "Error" or "Critical" or "Warning";
            }

            return false;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;

            var json = File.ReadAllText(_filePath);
            var state = JsonSerializer.Deserialize<PersistedState>(json, JsonOpts);
            if (state is null) return;

            _enabledContainers.Clear();
            if (state.EnabledContainerIds is not null)
            {
                foreach (var id in state.EnabledContainerIds)
                    _enabledContainers.Add(id);
            }

            _logModes.Clear();
            if (state.LogModes is not null)
            {
                foreach (var (id, mode) in state.LogModes)
                {
                    if (Enum.TryParse<LogMode>(mode, true, out var parsed))
                        _logModes[id] = parsed;
                }
            }

            _startDates.Clear();
            if (state.StartDates is not null)
            {
                foreach (var (id, date) in state.StartDates)
                    _startDates[id] = date;
            }
            _globalStartDate = state.GlobalStartDate;

            _logger.LogInformation("Loaded toggle state: {Enabled} enabled containers, {Modes} log modes",
                _enabledContainers.Count, _logModes.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load toggle state from {Path}", _filePath);
        }
    }

    private void Save()
    {
        try
        {
            var state = new PersistedState
            {
                EnabledContainerIds = _enabledContainers.ToList(),
                LogModes = _logModes.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase),
                StartDates = _startDates.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value,
                    StringComparer.OrdinalIgnoreCase),
                GlobalStartDate = _globalStartDate
            };

            var json = JsonSerializer.Serialize(state, JsonOpts);
            var dir = Path.GetDirectoryName(_filePath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save toggle state to {Path}", _filePath);
        }
    }

    private class PersistedState
    {
        public List<string>? EnabledContainerIds { get; set; }
        public Dictionary<string, string>? LogModes { get; set; }
        public Dictionary<string, DateTime>? StartDates { get; set; }
        public DateTime? GlobalStartDate { get; set; }
    }
}
