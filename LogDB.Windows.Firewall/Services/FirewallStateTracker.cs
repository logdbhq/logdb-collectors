using LogDB.Windows.Firewall.Models;
using Newtonsoft.Json;

namespace LogDB.Windows.Firewall.Services;

public class FirewallStateTracker
{
    private readonly string _stateFilePath;
    private readonly ILogger<FirewallStateTracker> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private FirewallState _state = new();

    public FirewallStateTracker(ILogger<FirewallStateTracker> logger)
    {
        _logger = logger;
        _stateFilePath = Path.Combine(AppContext.BaseDirectory, "firewall-state.json");
    }

    public async Task<FirewallState> LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (File.Exists(_stateFilePath))
            {
                var json = await File.ReadAllTextAsync(_stateFilePath);
                _state = JsonConvert.DeserializeObject<FirewallState>(json) ?? new FirewallState();
                _logger.LogInformation("Loaded state from {Path} ({Count} sources tracked)", _stateFilePath, _state.Sources.Count);
            }
            else
            {
                _state = new FirewallState();
                _logger.LogInformation("No existing state file, starting fresh");
            }
            return _state;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load state from {Path}", _stateFilePath);
            _state = new FirewallState();
            return _state;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateSourceStateAsync(string sourceId, string sourceName, int ipCount, int ruleCount, string status, string error = "")
    {
        await _lock.WaitAsync();
        try
        {
            _state.Sources[sourceId] = new SourceSyncState
            {
                SourceName = sourceName,
                LastSyncUtc = DateTime.UtcNow,
                IpCount = ipCount,
                RuleCount = ruleCount,
                Status = status,
                ErrorMessage = error
            };
            _state.LastFullSyncUtc = DateTime.UtcNow;
            _state.TotalRulesManaged = _state.Sources.Values.Sum(s => s.RuleCount);

            await SaveInternalAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveSourceAsync(string sourceId)
    {
        await _lock.WaitAsync();
        try
        {
            _state.Sources.Remove(sourceId);
            _state.TotalRulesManaged = _state.Sources.Values.Sum(s => s.RuleCount);
            await SaveInternalAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveInternalAsync()
    {
        try
        {
            var json = JsonConvert.SerializeObject(_state, Formatting.Indented);
            await File.WriteAllTextAsync(_stateFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save state to {Path}", _stateFilePath);
        }
    }
}
