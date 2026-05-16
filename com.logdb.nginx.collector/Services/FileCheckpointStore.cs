using System.Text.Json;
using com.logdb.nginx.collector.Configuration;
using com.logdb.nginx.collector.Models;
using Microsoft.Extensions.Options;

namespace com.logdb.nginx.collector.Services;

public class FileCheckpointStore : ICheckpointStore
{
    private readonly ILogger<FileCheckpointStore> _logger;
    private readonly CheckpointOptions _options;
    private readonly Lock _lock = new();
    private List<FileCheckpoint> _checkpoints = new();
    private bool _dirty;
    private DateTime? _lastFlushUtc;

    public FileCheckpointStore(ILogger<FileCheckpointStore> logger, IOptions<CheckpointOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public void Load()
    {
        if (!_options.Enabled) return;

        var path = _options.FilePath;
        if (!File.Exists(path))
        {
            _logger.LogInformation("No checkpoint file found at {Path}, starting fresh", path);
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            _checkpoints = JsonSerializer.Deserialize<List<FileCheckpoint>>(json) ?? new();
            _logger.LogInformation("Loaded {Count} checkpoints from {Path}", _checkpoints.Count, path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load checkpoints from {Path}", path);
            _checkpoints = new();
        }
    }

    public long GetOffset(string filePath)
    {
        lock (_lock)
        {
            var cp = _checkpoints.FirstOrDefault(c => c.FilePath == filePath);
            return cp?.Offset ?? 0;
        }
    }

    public FileCheckpoint? GetCheckpoint(string filePath)
    {
        lock (_lock)
        {
            return _checkpoints.FirstOrDefault(c => c.FilePath == filePath);
        }
    }

    public void UpdateCheckpoint(string filePath, string targetName, long offset, long fileSize, DateTime? fileCreatedUtc)
    {
        lock (_lock)
        {
            var cp = _checkpoints.FirstOrDefault(c => c.FilePath == filePath);
            if (cp is null)
            {
                cp = new FileCheckpoint { FilePath = filePath, TargetName = targetName };
                _checkpoints.Add(cp);
            }

            cp.Offset = offset;
            cp.TargetName = targetName;
            cp.FileSize = fileSize;
            cp.FileCreatedUtc = fileCreatedUtc;
            cp.LastWriteUtc = DateTime.UtcNow;
            cp.LastSeenUtc = DateTime.UtcNow;
            _dirty = true;
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || !_dirty) return;

        List<FileCheckpoint> snapshot;
        lock (_lock)
        {
            if (!_dirty) return;
            snapshot = new List<FileCheckpoint>(_checkpoints);
            _dirty = false;
        }

        try
        {
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            var tmpPath = _options.FilePath + ".tmp";
            await File.WriteAllTextAsync(tmpPath, json, cancellationToken);
            File.Move(tmpPath, _options.FilePath, overwrite: true);
            _lastFlushUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush checkpoints");
            lock (_lock) { _dirty = true; }
        }
    }

    public IReadOnlyList<FileCheckpoint> GetCheckpoints()
    {
        lock (_lock) { return new List<FileCheckpoint>(_checkpoints); }
    }

    public CheckpointStatus GetStatus()
    {
        lock (_lock)
        {
            return new CheckpointStatus
            {
                Enabled = _options.Enabled,
                TrackedFiles = _checkpoints.Count,
                LastFlushUtc = _lastFlushUtc,
                FilePath = _options.FilePath
            };
        }
    }
}
