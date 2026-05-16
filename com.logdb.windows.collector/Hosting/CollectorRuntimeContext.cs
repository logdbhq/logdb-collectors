using com.logdb.windows.collector.shared.Contracts;

namespace com.logdb.windows.collector.Hosting;

public sealed class CollectorRuntimeContext
{
    public CollectorRuntimeContext(
        CollectorInstanceMode mode,
        string controlPipeName,
        string configPath,
        string serviceName)
    {
        Mode = mode;
        ControlPipeName = controlPipeName;
        ConfigPath = configPath;
        ServiceName = serviceName;
        StartedAtUtc = DateTime.UtcNow;
        ProcessId = Environment.ProcessId;
    }

    public CollectorInstanceMode Mode { get; }
    public string ControlPipeName { get; }
    public string ConfigPath { get; }
    public string ServiceName { get; }
    public int ProcessId { get; }
    public DateTime StartedAtUtc { get; }
}
