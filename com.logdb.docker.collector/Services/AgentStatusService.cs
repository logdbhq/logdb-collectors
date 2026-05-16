using com.logdb.docker.collector.Configuration;
using com.logdb.docker.collector.Models;
using Microsoft.Extensions.Options;

namespace com.logdb.docker.collector.Services;

public class AgentStatusService
{
    private readonly DateTime _startTime = DateTime.UtcNow;
    private readonly StartupValidator _validator;
    private readonly string _environment;
    private readonly string _dockerEndpoint;

    public AgentStatusService(
        StartupValidator validator,
        IOptions<DockerDiscoveryOptions> discoveryOptions,
        IHostEnvironment hostEnvironment)
    {
        _validator = validator;
        _environment = hostEnvironment.EnvironmentName;
        _dockerEndpoint = discoveryOptions.Value.DockerEndpoint ?? GetDefaultEndpoint();
    }

    public AgentStatus GetStatus()
    {
        return new AgentStatus
        {
            Version = BuildInfo.Version,
            BuildDate = BuildInfo.BuildDate,
            CommitHash = BuildInfo.CommitHash,
            Environment = _environment,
            DockerEndpoint = _dockerEndpoint,
            UptimeSeconds = Math.Round((DateTime.UtcNow - _startTime).TotalSeconds, 1),
            AgentState = _validator.HasErrors ? "degraded" : "running",
            Warnings = _validator.Warnings.ToList(),
            Errors = _validator.Errors.ToList()
        };
    }

    public ReadinessResult GetReadiness()
    {
        var ready = _validator.Validated && !_validator.HasErrors;

        return new ReadinessResult
        {
            Ready = ready,
            Warnings = _validator.Warnings.ToList(),
            Errors = _validator.Errors.ToList()
        };
    }

    private static string GetDefaultEndpoint()
    {
        return OperatingSystem.IsWindows()
            ? "npipe://./pipe/docker_engine"
            : "unix:///var/run/docker.sock";
    }
}
