using com.logdb.docker.collector.Models;

namespace com.logdb.docker.collector.Services;

public interface ICheckpointStore
{
    void Load();
    long GetOffset(string filePath);
    void UpdateCheckpoint(string filePath, string containerId, string containerName, long offset);
    Task FlushAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<FileCheckpoint> GetCheckpoints();
    CheckpointStatus GetStatus();
    void Clear();
    void ClearForContainer(string containerId);
}
