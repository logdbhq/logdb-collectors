using System.Text.Json;
using com.logdb.docker.collector.Configuration;
using com.logdb.docker.collector.Models;
using Microsoft.Extensions.Options;

namespace com.logdb.docker.collector.Services;

public class FileCheckpointStore : ICheckpointStore
{
    private readonly ILogger<FileCheckpointStore> _logger;
    private readonly CheckpointOptions _options;
    private readonly string _filePath;
    private readonly object _lock = new();

    private Dictionary<string, FileCheckpoint> _checkpoints = new();
    private bool _dirty;
    private DateTime? _lastFlushUtc;
    private string? _error;
    private int _loadedCount;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FileCheckpointStore(ILogger<FileCheckpointStore> logger, IOptions<CheckpointOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        _filePath = Path.GetFullPath(_options.FilePath);
    }

    public void Load()
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Checkpoints disabled");
            return;
        }

        try
        {
            if (!File.Exists(_filePath))
            {
                _logger.LogDebug("No checkpoint file, starting fresh");
                return;
            }

            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<List<FileCheckpoint>>(json, JsonOptions);

            if (loaded is not null)
            {
                lock (_lock)
                {
                    _checkpoints = loaded.ToDictionary(c => c.FilePath, c => c);
                    _loadedCount = _checkpoints.Count;
                    _error = null;
                }
                _logger.LogInformation("Loaded {Count} checkpoints", loaded.Count);
            }
        }
        catch (Exception ex)
        {
            lock (_lock) { _error = ex.Message; }
            _logger.LogWarning("Failed to load checkpoints: {Msg}", ex.Message);
        }
    }

    public long GetOffset(string filePath)
    {
        lock (_lock)
        {
            return _checkpoints.TryGetValue(filePath, out var cp) ? cp.Offset : 0;
        }
    }

    public void UpdateCheckpoint(string filePath, string containerId, string containerName, long offset)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_checkpoints.TryGetValue(filePath, out var existing))
            {
                existing.Offset = offset;
                existing.ContainerId = containerId;
                existing.ContainerName = containerName;
                existing.LastWriteUtc = now;
                existing.LastSeenUtc = now;
            }
            else
            {
                _checkpoints[filePath] = new FileCheckpoint
                {
                    FilePath = filePath,
                    ContainerId = containerId,
                    ContainerName = containerName,
                    Offset = offset,
                    LastWriteUtc = now,
                    LastSeenUtc = now
                };
            }
            _dirty = true;
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return;

        List<FileCheckpoint> snapshot;
        lock (_lock)
        {
            if (!_dirty) return;
            snapshot = _checkpoints.Values.ToList();
            _dirty = false;
        }

        try
        {
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            var tempPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, _filePath, overwrite: true);

            lock (_lock)
            {
                _lastFlushUtc = DateTime.UtcNow;
                _error = null;
            }

            _logger.LogTrace("Flushed {Count} checkpoints", snapshot.Count);
        }
        catch (Exception ex)
        {
            lock (_lock) { _error = ex.Message; }
            _logger.LogWarning("Checkpoint flush failed: {Msg}", ex.Message);
        }
    }

    public IReadOnlyList<FileCheckpoint> GetCheckpoints()
    {
        lock (_lock)
        {
            return _checkpoints.Values.ToList();
        }
    }

    public CheckpointStatus GetStatus()
    {
        lock (_lock)
        {
            return new CheckpointStatus
            {
                Enabled = _options.Enabled,
                CheckpointFilePath = _filePath,
                LoadedCount = _loadedCount,
                LastFlushUtc = _lastFlushUtc,
                Error = _error
            };
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _checkpoints.Clear();
            _dirty = true;
            _loadedCount = 0;
            _logger.LogInformation("All checkpoints cleared");
        }

        // Flush immediately so the file reflects the cleared state
        FlushAsync().GetAwaiter().GetResult();
    }

    public void ClearForContainer(string containerId)
    {
        lock (_lock)
        {
            var keysToRemove = _checkpoints
                .Where(kv => kv.Value.ContainerId.Equals(containerId, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in keysToRemove)
                _checkpoints.Remove(key);

            if (keysToRemove.Count > 0)
            {
                _dirty = true;
                _logger.LogInformation("Cleared {Count} checkpoints for container {ContainerId}", keysToRemove.Count, containerId);
            }
        }

        FlushAsync().GetAwaiter().GetResult();
    }
}
