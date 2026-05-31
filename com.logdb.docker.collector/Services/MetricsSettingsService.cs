using System.Text.Json;
using com.logdb.docker.collector.Configuration;
using Microsoft.Extensions.Options;

namespace com.logdb.docker.collector.Services;

/// <summary>
/// Holds the runtime-adjustable metrics collection interval. Seeded from
/// <see cref="DockerMetricsOptions"/> and persisted (next to the checkpoints,
/// on the Docker volume) so a value set from the UI survives restarts.
/// </summary>
public class MetricsSettingsService
{
    public const int MinIntervalSeconds = 10;
    public const int MaxIntervalSeconds = 86_400; // 24h

    private readonly ILogger<MetricsSettingsService> _logger;
    private readonly string _filePath;
    private readonly object _lock = new();
    private int _intervalSeconds;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MetricsSettingsService(
        ILogger<MetricsSettingsService> logger,
        IOptions<DockerMetricsOptions> options,
        IOptions<CheckpointOptions> checkpointOptions)
    {
        _logger = logger;
        _intervalSeconds = Clamp(options.Value.CollectionIntervalSeconds);

        // Persist next to checkpoints (on the Docker volume), like container-toggles.json
        var checkpointDir = Path.GetDirectoryName(Path.GetFullPath(checkpointOptions.Value.FilePath)) ?? ".";
        _filePath = Path.Combine(checkpointDir, "metrics-settings.json");
        Load();
    }

    public int IntervalSeconds
    {
        get { lock (_lock) { return _intervalSeconds; } }
    }

    public void SetIntervalSeconds(int seconds)
    {
        var clamped = Clamp(seconds);
        lock (_lock)
        {
            _intervalSeconds = clamped;
            Save();
        }
        _logger.LogInformation("Metrics collection interval updated to {Seconds}s", clamped);
    }

    private static int Clamp(int seconds) =>
        Math.Clamp(seconds, MinIntervalSeconds, MaxIntervalSeconds);

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;

            var json = File.ReadAllText(_filePath);
            var state = JsonSerializer.Deserialize<PersistedState>(json, JsonOpts);
            if (state?.IntervalSeconds is int saved)
            {
                _intervalSeconds = Clamp(saved);
                _logger.LogInformation("Loaded metrics interval override: {Seconds}s", _intervalSeconds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load metrics settings from {Path}", _filePath);
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(new PersistedState { IntervalSeconds = _intervalSeconds }, JsonOpts);
            var dir = Path.GetDirectoryName(_filePath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save metrics settings to {Path}", _filePath);
        }
    }

    private class PersistedState
    {
        public int? IntervalSeconds { get; set; }
    }
}
