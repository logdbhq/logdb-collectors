using com.logdb.nginx.collector.Models;

namespace com.logdb.nginx.collector.Services;

public interface ICheckpointStore
{
    void Load();
    long GetOffset(string filePath);
    FileCheckpoint? GetCheckpoint(string filePath);
    void UpdateCheckpoint(string filePath, string targetName, long offset, long fileSize, DateTime? fileCreatedUtc);
    Task FlushAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<FileCheckpoint> GetCheckpoints();
    CheckpointStatus GetStatus();
}
