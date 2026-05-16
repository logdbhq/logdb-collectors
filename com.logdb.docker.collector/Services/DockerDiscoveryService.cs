using Docker.DotNet;
using Docker.DotNet.Models;
using com.logdb.docker.collector.Configuration;
using com.logdb.docker.collector.Models;
using Microsoft.Extensions.Options;

namespace com.logdb.docker.collector.Services;

public class DockerDiscoveryService : IDockerDiscoveryService
{
    private readonly ILogger<DockerDiscoveryService> _logger;
    private readonly ContainerFilterService _filterService;
    private readonly string _endpoint;
    private readonly DockerClient _client;
    private readonly object _lock = new();

    private List<ContainerInfo> _containers = new();
    private bool _available;
    private string? _error;
    private DateTime? _lastRefreshUtc;

    public DockerDiscoveryService(
        ILogger<DockerDiscoveryService> logger,
        IOptions<DockerDiscoveryOptions> options,
        ContainerFilterService filterService)
    {
        _logger = logger;
        _filterService = filterService;
        _endpoint = options.Value.DockerEndpoint ?? GetDefaultEndpoint();

        _client = new DockerClientConfiguration(new Uri(_endpoint))
            .CreateClient();
    }

    public DockerStatus GetDockerStatus()
    {
        lock (_lock)
        {
            return new DockerStatus
            {
                Available = _available,
                Endpoint = _endpoint,
                LastRefreshUtc = _lastRefreshUtc,
                ContainerCount = _containers.Count,
                Error = _error
            };
        }
    }

    public IReadOnlyList<ContainerInfo> GetContainers()
    {
        lock (_lock)
        {
            return _containers.ToList();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var listParams = new ContainersListParameters { All = true };
            var responses = await _client.Containers.ListContainersAsync(listParams, cancellationToken);

            var containers = new List<ContainerInfo>();
            foreach (var r in responses)
            {
                var info = MapContainer(r);

                // Inspect for log path
                try
                {
                    var inspect = await _client.Containers.InspectContainerAsync(r.ID, cancellationToken);
                    info.LogPath = inspect.LogPath ?? "";
                }
                catch
                {
                    // Non-critical - container may have been removed between list and inspect
                }

                containers.Add(info);
            }

            _filterService.Apply(containers);

            lock (_lock)
            {
                _containers = containers;
                _available = true;
                _error = null;
                _lastRefreshUtc = DateTime.UtcNow;
            }

            _logger.LogDebug("Docker discovery refreshed: {Count} containers ({Included} included)",
                containers.Count, containers.Count(c => c.IsIncluded));
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _available = false;
                _error = ex.Message;
                _lastRefreshUtc = DateTime.UtcNow;
            }

            _logger.LogWarning(ex, "Docker discovery failed");
        }
    }

    private static ContainerInfo MapContainer(ContainerListResponse r)
    {
        var name = r.Names.FirstOrDefault()?.TrimStart('/') ?? r.ID[..12];
        var (image, tag) = ParseImageTag(r.Image);
        r.Labels ??= new Dictionary<string, string>();

        return new ContainerInfo
        {
            Id = r.ID[..12],
            Name = name,
            Image = image,
            ImageTag = tag,
            State = r.State,
            Status = r.Status,
            CreatedAt = r.Created,
            Labels = new Dictionary<string, string>(r.Labels),
            ComposeProject = r.Labels.TryGetValue("com.docker.compose.project", out var proj) ? proj : null,
            ComposeService = r.Labels.TryGetValue("com.docker.compose.service", out var svc) ? svc : null,
            IsIncluded = true,
            ExclusionReason = null
        };
    }

    private static (string Image, string Tag) ParseImageTag(string imageRef)
    {
        if (string.IsNullOrEmpty(imageRef))
            return ("unknown", "unknown");

        // Remove sha256 digest references
        var atIdx = imageRef.IndexOf('@');
        if (atIdx >= 0)
            imageRef = imageRef[..atIdx];

        var colonIdx = imageRef.LastIndexOf(':');
        if (colonIdx < 0 || imageRef.LastIndexOf('/') > colonIdx)
            return (imageRef, "latest");

        return (imageRef[..colonIdx], imageRef[(colonIdx + 1)..]);
    }

    private static string GetDefaultEndpoint()
    {
        return OperatingSystem.IsWindows()
            ? "npipe://./pipe/docker_engine"
            : "unix:///var/run/docker.sock";
    }
}
