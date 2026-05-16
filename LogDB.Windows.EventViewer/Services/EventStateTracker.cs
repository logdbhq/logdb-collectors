using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace com.logdb.windows.eventviewer.Services;

/// <summary>
/// Tracks per-source event export state in a local JSON file.
/// Replaces the remote ExporterStateApiClient.
/// </summary>
public class EventStateTracker
{
    private readonly ILogger<EventStateTracker>? _logger;
    private readonly string _stateFilePath;
    private EventStateData _state;
    private readonly object _lock = new();

    public EventStateTracker(ILogger<EventStateTracker>? logger = null)
    {
        _logger = logger;

        var exeDir = AppContext.BaseDirectory;
        _stateFilePath = Path.Combine(exeDir, "eventviewer-state.json");

        _state = LoadState();
    }

    /// <summary>
    /// Get state for a specific log source.
    /// </summary>
    public SourceState? GetSourceState(string logSource)
    {
        lock (_lock)
        {
            _state.Sources.TryGetValue(logSource, out var state);
            return state;
        }
    }

    /// <summary>
    /// Update state after successfully processing events from a source.
    /// </summary>
    public void UpdateSourceState(string logSource, DateTime lastTimestamp, long lastEventId, long totalExported)
    {
        lock (_lock)
        {
            _state.Sources[logSource] = new SourceState
            {
                LastTimestamp = lastTimestamp.Kind == DateTimeKind.Utc
                    ? lastTimestamp
                    : lastTimestamp.ToUniversalTime(),
                LastEventId = lastEventId,
                TotalExported = totalExported,
                LastExportAt = DateTime.UtcNow
            };

            SaveState();
        }
    }

    /// <summary>
    /// Clear all tracked state (for --reset flag).
    /// </summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            _state = new EventStateData();
            SaveState();
        }

        _logger?.LogInformation("Cleared all event export state");
    }

    private EventStateData LoadState()
    {
        try
        {
            if (File.Exists(_stateFilePath))
            {
                var json = File.ReadAllText(_stateFilePath);
                var state = JsonSerializer.Deserialize<EventStateData>(json);
                if (state != null)
                {
                    _logger?.LogInformation("Loaded state for {Count} sources from {Path}",
                        state.Sources.Count, _stateFilePath);
                    return state;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load state from {Path}, starting fresh", _stateFilePath);
        }

        return new EventStateData();
    }

    private void SaveState()
    {
        try
        {
            var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_stateFilePath, json);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save state to {Path}", _stateFilePath);
        }
    }
}

public class EventStateData
{
    public Dictionary<string, SourceState> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class SourceState
{
    public DateTime LastTimestamp { get; set; }
    public long LastEventId { get; set; }
    public long TotalExported { get; set; }
    public DateTime LastExportAt { get; set; }
}
