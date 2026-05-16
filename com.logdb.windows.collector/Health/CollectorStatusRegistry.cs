using System.Collections.Concurrent;
using com.logdb.windows.collector.shared.Contracts;

namespace com.logdb.windows.collector.Health;

public sealed class CollectorStatusRegistry
{
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private readonly string _configPath;
    private readonly CollectorInstanceMode _instanceMode;
    private readonly string _controlPipeName;
    private readonly int _processId;
    private readonly string _serviceName;
    private readonly ConcurrentDictionary<string, ModuleStatusDto> _modules = new(StringComparer.OrdinalIgnoreCase);

    public CollectorStatusRegistry(
        string configPath,
        CollectorInstanceMode instanceMode,
        string controlPipeName,
        int processId,
        string serviceName)
    {
        _configPath = configPath;
        _instanceMode = instanceMode;
        _controlPipeName = controlPipeName;
        _processId = processId;
        _serviceName = serviceName;
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
