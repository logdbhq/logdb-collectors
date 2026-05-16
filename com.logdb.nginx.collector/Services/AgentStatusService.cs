using com.logdb.nginx.collector.Models;

namespace com.logdb.nginx.collector.Services;

public class AgentStatusService
{
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly List<string> _errors = new();
    private readonly List<string> _warnings = new();
    private string _state = "starting";

    private string _buildDate = "";
    private string _commitHash = "";
    private string _environment = "";
    private int _targetCount;
    private int _activeFiles;

    private bool _configLoaded;
    private bool _checkpointInitialized;
    private bool _spoolInitialized;

    public void SetState(string state) => _state = state;
    public void AddError(string error) { lock (_errors) _errors.Add(error); }
    public void AddWarning(string warning) { lock (_warnings) _warnings.Add(warning); }

    public void SetBuildInfo(string buildDate, string commitHash, string environment)
    {
        _buildDate = buildDate;
        _commitHash = commitHash;
        _environment = environment;
    }

    public void SetTargetInfo(int targetCount, int activeFiles)
    {
        _targetCount = targetCount;
        _activeFiles = activeFiles;
    }

    public void SetConfigLoaded() => _configLoaded = true;
    public void SetCheckpointInitialized() => _checkpointInitialized = true;
    public void SetSpoolInitialized() => _spoolInitialized = true;

    public AgentStatus GetStatus()
    {
        return new AgentStatus
        {
            Version = typeof(AgentStatusService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            BuildDate = _buildDate,
            CommitHash = _commitHash,
            Environment = _environment,
            State = _state,
            StartedUtc = _startedUtc,
            Uptime = DateTime.UtcNow - _startedUtc,
            TargetCount = _targetCount,
            ActiveFiles = _activeFiles,
            Errors = new List<string>(_errors),
            Warnings = new List<string>(_warnings)
        };
    }

    public ReadinessResult GetReadiness()
    {
        var errors = new List<string>();

        if (!_configLoaded) errors.Add("Configuration not loaded");
        if (!_checkpointInitialized) errors.Add("Checkpoint store not initialized");
        if (!_spoolInitialized) errors.Add("Spool store not initialized");

        lock (_errors)
        {
            errors.AddRange(_errors);
        }

        return new ReadinessResult
        {
            Ready = errors.Count == 0,
            Errors = errors
        };
    }
}
