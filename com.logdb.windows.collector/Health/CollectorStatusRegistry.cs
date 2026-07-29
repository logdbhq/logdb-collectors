using System.Collections.Concurrent;
using System.Text.Json;
using com.logdb.windows.collector.shared.Contracts;

namespace com.logdb.windows.collector.Health;

public sealed class CollectorStatusRegistry
{
    private static readonly JsonSerializerOptions FailureJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private readonly string _configPath;
    private readonly CollectorInstanceMode _instanceMode;
    private readonly string _controlPipeName;
    private readonly int _processId;
    private readonly string _serviceName;
    private readonly ConcurrentDictionary<string, ModuleStatusDto> _modules = new(StringComparer.OrdinalIgnoreCase);

    // Bounded history of recent failures so the UI can drill into the
    // "Critical Issues" counter. Newest entries are appended at the tail;
    // the oldest are evicted once the cap is reached. When a path is supplied
    // the buffer is restored on startup and rewritten on every failure so the
    // history survives service restarts.
    private const int MaxFailureHistory = 250;
    private readonly object _failureGate = new();
    private readonly LinkedList<CollectorFailureDto> _failures = new();
    private readonly string? _failureLogPath;

    public CollectorStatusRegistry(
        string configPath,
        CollectorInstanceMode instanceMode,
        string controlPipeName,
        int processId,
        string serviceName,
        string? failureLogPath = null)
    {
        _configPath = configPath;
        _instanceMode = instanceMode;
        _controlPipeName = controlPipeName;
        _processId = processId;
        _serviceName = serviceName;
        _failureLogPath = failureLogPath;

        LoadFailuresFromDisk();
    }

    public void RegisterModule(string moduleName)
    {
        _modules.TryAdd(moduleName, new ModuleStatusDto
        {
            Name = moduleName,
            State = "Stopped",
            UpdatedAtUtc = DateTime.UtcNow
        });
    }

    public void SetEnabled(string moduleName, bool enabled)
    {
        var module = _modules.GetOrAdd(moduleName, name => new ModuleStatusDto { Name = name });
        module.Enabled = enabled;
        module.UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkRunning(string moduleName)
    {
        var module = _modules.GetOrAdd(moduleName, name => new ModuleStatusDto { Name = name });
        module.State = "Running";
        module.LastError = null;
        module.LastSuccessTimeUtc = DateTime.UtcNow;
        module.SentCount++;
        module.UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkHeartbeat(string moduleName)
    {
        var module = _modules.GetOrAdd(moduleName, name => new ModuleStatusDto { Name = name });
        module.LastSuccessTimeUtc = DateTime.UtcNow;
        module.UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkStopped(string moduleName, string state = "Stopped")
    {
        var module = _modules.GetOrAdd(moduleName, name => new ModuleStatusDto { Name = name });
        module.State = state;
        module.UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkError(string moduleName, string error)
    {
        var module = _modules.GetOrAdd(moduleName, name => new ModuleStatusDto { Name = name });
        module.State = "Error";
        module.LastError = error;
        module.FailedCount++;
        module.UpdatedAtUtc = DateTime.UtcNow;

        lock (_failureGate)
        {
            _failures.AddLast(new CollectorFailureDto
            {
                TimestampUtc = DateTime.UtcNow,
                Module = moduleName,
                Error = error
            });

            while (_failures.Count > MaxFailureHistory)
            {
                _failures.RemoveFirst();
            }

            PersistFailuresLocked();
        }
    }

    /// <summary>
    /// Returns the most recent failures, newest first, capped at
    /// <paramref name="max"/> entries.
    /// </summary>
    public IReadOnlyList<CollectorFailureDto> RecentFailures(int max)
    {
        if (max <= 0)
        {
            return Array.Empty<CollectorFailureDto>();
        }

        lock (_failureGate)
        {
            return _failures
                .Reverse()
                .Take(max)
                .Select(failure => new CollectorFailureDto
                {
                    TimestampUtc = failure.TimestampUtc,
                    Module = failure.Module,
                    Error = failure.Error
                })
                .ToList();
        }
    }

    private void LoadFailuresFromDisk()
    {
        if (string.IsNullOrWhiteSpace(_failureLogPath) || !File.Exists(_failureLogPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_failureLogPath);
            var restored = JsonSerializer.Deserialize<List<CollectorFailureDto>>(json, FailureJsonOptions);
            if (restored == null)
            {
                return;
            }

            lock (_failureGate)
            {
                foreach (var failure in restored
                    .OrderBy(failure => failure.TimestampUtc)
                    .TakeLast(MaxFailureHistory))
                {
                    _failures.AddLast(failure);
                }
            }
        }
        catch
        {
            // Best-effort restore: a missing/corrupt history file must never
            // prevent the collector from starting.
        }
    }

    // Caller must hold _failureGate. Writes the full buffer atomically so a
    // crash mid-write can't leave a truncated history file.
    private void PersistFailuresLocked()
    {
        if (string.IsNullOrWhiteSpace(_failureLogPath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_failureLogPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_failures.ToList(), FailureJsonOptions);
            var tempPath = _failureLogPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _failureLogPath, overwrite: true);
        }
        catch
        {
            // Best-effort persistence: failing to write history must never
            // disrupt collection or surface as a new error.
        }
    }

    public CollectorStatusDto Snapshot()
    {
        return new CollectorStatusDto
        {
            ServiceName = _serviceName,
            InstanceMode = _instanceMode,
            ControlPipeName = _controlPipeName,
            ProcessId = _processId,
            StartedAtUtc = _startedAtUtc,
            UtcNow = DateTime.UtcNow,
            ConfigPath = _configPath,
            Modules = _modules.Values
                .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase)
                .Select(Clone)
                .ToList()
        };
    }

    private static ModuleStatusDto Clone(ModuleStatusDto source)
    {
        return new ModuleStatusDto
        {
            Name = source.Name,
            Enabled = source.Enabled,
            State = source.State,
            LastSuccessTimeUtc = source.LastSuccessTimeUtc,
            LastError = source.LastError,
            SentCount = source.SentCount,
            FailedCount = source.FailedCount,
            UpdatedAtUtc = source.UpdatedAtUtc
        };
    }
}
