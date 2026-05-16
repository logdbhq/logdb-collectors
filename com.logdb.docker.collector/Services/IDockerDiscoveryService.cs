using com.logdb.docker.collector.Models;

namespace com.logdb.docker.collector.Services;

public interface IDockerDiscoveryService
{
    DockerStatus GetDockerStatus();
    IReadOnlyList<ContainerInfo> GetContainers();
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
