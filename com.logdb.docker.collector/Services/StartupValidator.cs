using com.logdb.docker.collector.Configuration;
using Microsoft.Extensions.Options;

namespace com.logdb.docker.collector.Services;

public class StartupValidator
{
    private readonly List<string> _warnings = new();
    private readonly List<string> _errors = new();
    private bool _validated;

    public IReadOnlyList<string> Warnings => _warnings;
    public IReadOnlyList<string> Errors => _errors;
    public bool HasErrors => _errors.Count > 0;
    public bool Validated => _validated;

    public void Validate(
        IOptions<LogDbExporterOptions> exporterOptions,
        IOptions<CheckpointOptions> checkpointOptions,
        IOptions<SpoolOptions> spoolOptions,
        IOptions<DockerDiscoveryOptions> discoveryOptions,
        ILogger logger)
    {
        _warnings.Clear();
        _errors.Clear();

        var exporter = exporterOptions.Value;
        var checkpoint = checkpointOptions.Value;
        var spool = spoolOptions.Value;

        // Exporter validation
        if (exporter.Enabled)
        {
            if (string.IsNullOrWhiteSpace(exporter.Endpoint) || exporter.Endpoint == "http://localhost:5080")
                _warnings.Add("Exporter is enabled but endpoint appears to be default (http://localhost:5080)");

            if (string.IsNullOrWhiteSpace(exporter.ApiKey))
                _warnings.Add("Exporter is enabled but API key is empty");
        }
        else
        {
            _warnings.Add("Exporter is disabled - logs will accumulate in the spool but not be sent");
        }

        // Docker socket
        var dockerEndpoint = discoveryOptions.Value.DockerEndpoint ?? GetDefaultDockerEndpoint();
        if (dockerEndpoint.StartsWith("unix://"))
        {
            var socketPath = dockerEndpoint.Replace("unix://", "");
            if (!File.Exists(socketPath))
                _errors.Add($"Docker socket not found at {socketPath}");
        }
        else if (dockerEndpoint.StartsWith("npipe://"))
        {
            // Windows named pipe - can't easily test, just note it
        }

        // Docker log directory (Linux container typical path)
        var dockerLogDir = "/var/lib/docker/containers";
        if (OperatingSystem.IsLinux() && !Directory.Exists(dockerLogDir))
            _warnings.Add($"Docker log directory not found at {dockerLogDir} - ensure it is mounted read-only");

        // Checkpoint path
        if (checkpoint.Enabled)
        {
            var checkpointDir = Path.GetDirectoryName(Path.GetFullPath(checkpoint.FilePath));
            if (checkpointDir is not null)
            {
                try
                {
                    Directory.CreateDirectory(checkpointDir);
                    TestWrite(checkpointDir);
                }
                catch (Exception ex)
                {
                    _errors.Add($"Checkpoint directory is not writable ({checkpointDir}): {ex.Message}");
                }
            }
        }

        // Spool directory
        if (spool.Enabled)
        {
            var spoolDir = Path.GetFullPath(spool.DirectoryPath);
            try
            {
                Directory.CreateDirectory(spoolDir);
                TestWrite(spoolDir);
            }
            catch (Exception ex)
            {
                _errors.Add($"Spool directory is not writable ({spoolDir}): {ex.Message}");
            }
        }

        _validated = true;

        // Log diagnostics
        foreach (var w in _warnings)
            logger.LogWarning("Startup check: {Warning}", w);
        foreach (var e in _errors)
            logger.LogError("Startup check: {Error}", e);

        if (_errors.Count == 0 && _warnings.Count == 0)
            logger.LogInformation("Startup validation passed with no issues");
        else
            logger.LogInformation("Startup validation: {Warnings} warning(s), {Errors} error(s)",
                _warnings.Count, _errors.Count);
    }

    private static void TestWrite(string directory)
    {
        var testFile = Path.Combine(directory, ".write-test");
        try
        {
            File.WriteAllText(testFile, "ok");
        }
        finally
        {
            try { File.Delete(testFile); } catch { /* best effort cleanup */ }
        }
    }

    private static string GetDefaultDockerEndpoint()
    {
        return OperatingSystem.IsWindows()
            ? "npipe://./pipe/docker_engine"
            : "unix:///var/run/docker.sock";
    }
}
