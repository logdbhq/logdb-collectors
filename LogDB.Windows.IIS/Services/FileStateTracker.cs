using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace com.logdb.windows.iis.Services;

/// <summary>
/// Tracks file processing state using file size, last modified time, and byte position.
/// No longer hashes entire files - uses cheap filesystem metadata for change detection.
/// State is persisted to a local JSON file.
/// </summary>
public class FileStateTracker
{
    private readonly ILogger<FileStateTracker>? _logger;
    private readonly string _stateFilePath;
    private FileStateData _state;
    private readonly object _lock = new();

    public FileStateTracker(ILogger<FileStateTracker>? logger = null)
    {
        _logger = logger;

        var exeDir = AppContext.BaseDirectory;
        _stateFilePath = Path.Combine(exeDir, "iis-exporter-state.json");

        _state = LoadState();
    }

    /// <summary>
    /// Check if a file needs processing by comparing size and last modified time.
    /// Returns (needsProcessing, lastBytePosition, lastLogTimestamp).
    /// </summary>
    public (bool NeedsProcessing, long LastBytePosition, DateTime? LastLogTimestamp) CheckFile(string filePath)
    {
        if (!File.Exists(filePath))
            return (false, 0, null);

        var fileInfo = new FileInfo(filePath);

        lock (_lock)
        {
            if (_state.Files.TryGetValue(filePath, out var fileState))
            {
                if (fileState.FileSize == fileInfo.Length &&
                    fileState.LastModifiedUtc == fileInfo.LastWriteTimeUtc)
                {
                    _logger?.LogDebug("File unchanged: {FilePath}", filePath);
                    return (false, fileState.LastBytePosition, fileState.LastLogTimestamp);
                }

                // File shrank - likely rotated, start from beginning
                if (fileInfo.Length < fileState.LastBytePosition)
                {
                    _logger?.LogInformation("File was rotated: {FilePath}", filePath);
                    return (true, 0, null);
                }

                // File grew or was modified - resume from last position
                _logger?.LogDebug("File changed: {FilePath} (size: {OldSize} -> {NewSize})",
                    filePath, fileState.FileSize, fileInfo.Length);
                return (true, fileState.LastBytePosition, fileState.LastLogTimestamp);
            }

            // New file. Debug, not Information: on a reset/first run every historical
            // log file is "new", which floods the live console with one line each.
            _logger?.LogDebug("New file detected: {FilePath}", filePath);
            return (true, 0, null);
        }
    }

    /// <summary>
    /// Update state after successfully processing a file.
    /// </summary>
    public void UpdateFileState(string filePath, long bytePosition, DateTime? lastLogTimestamp)
    {
        if (!File.Exists(filePath))
            return;

        var fileInfo = new FileInfo(filePath);

        lock (_lock)
        {
            _state.Files[filePath] = new FileState
            {
                FileSize = fileInfo.Length,
                LastModifiedUtc = fileInfo.LastWriteTimeUtc,
                LastBytePosition = bytePosition,
                LastLogTimestamp = lastLogTimestamp,
                LastProcessedAt = DateTime.UtcNow
            };

            SaveState();
        }

        _logger?.LogDebug("Updated state for {FilePath}: pos={Position}, lastLog={Timestamp}",
            filePath, bytePosition, lastLogTimestamp);
    }

    /// <summary>
    /// Clear all tracked state (for --reset flag).
    /// </summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            _state = new FileStateData();
            SaveState();
        }

        _logger?.LogInformation("Cleared all file tracking state");
    }

    /// <summary>
    /// Remove state for a specific file.
    /// </summary>
    public void RemoveFile(string filePath)
    {
        lock (_lock)
        {
            if (_state.Files.Remove(filePath))
            {
                SaveState();
                _logger?.LogDebug("Removed state for {FilePath}", filePath);
            }
        }
    }

    private FileStateData LoadState()
    {
        try
        {
            if (File.Exists(_stateFilePath))
            {
                var json = File.ReadAllText(_stateFilePath);
                var state = JsonSerializer.Deserialize<FileStateData>(json);
                if (state != null)
                {
                    _logger?.LogInformation("Loaded state for {Count} files from {Path}",
                        state.Files.Count, _stateFilePath);
                    return state;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load state from {Path}, starting fresh", _stateFilePath);
        }

        return new FileStateData();
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

public class FileStateData
{
    public Dictionary<string, FileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class FileState
{
    public long FileSize { get; set; }
    public DateTime LastModifiedUtc { get; set; }
    public long LastBytePosition { get; set; }
    public DateTime? LastLogTimestamp { get; set; }
    public DateTime LastProcessedAt { get; set; }
}
