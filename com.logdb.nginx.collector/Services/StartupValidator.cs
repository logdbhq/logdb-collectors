using com.logdb.nginx.collector.Configuration;
using Microsoft.Extensions.Options;

namespace com.logdb.nginx.collector.Services;

public class StartupValidator
{
    private readonly AgentStatusService _agentStatus;

    public StartupValidator(AgentStatusService agentStatus)
    {
        _agentStatus = agentStatus;
    }

    public void Validate(
        IOptions<LogDbExporterOptions> exporterOpts,
        IOptions<CheckpointOptions> checkpointOpts,
        IOptions<SpoolOptions> spoolOpts,
        IOptions<NginxTargetOptions> targetOpts,
        ILogger logger)
    {
        var exporter = exporterOpts.Value;
        var checkpoint = checkpointOpts.Value;
        var spool = spoolOpts.Value;
        var targets = targetOpts.Value;

        // Validate exporter
        if (exporter.Enabled)
        {
            if (string.IsNullOrWhiteSpace(exporter.Endpoint) || exporter.Endpoint == "http://localhost:5000")
            {
                _agentStatus.AddError("LogDB exporter is enabled but endpoint is not configured");
                logger.LogError("LogDB exporter is enabled but endpoint is not configured");
            }
            if (string.IsNullOrWhiteSpace(exporter.ApiKey))
            {
                _agentStatus.AddError("LogDB exporter is enabled but API key is missing");
                logger.LogError("LogDB exporter is enabled but API key is missing");
            }
            else if (IsPlaceholderApiKey(exporter.ApiKey))
            {
                _agentStatus.AddError($"LogDB exporter is enabled but API key is the placeholder value '{exporter.ApiKey}' - set LOGDB_EXPORTER_APIKEY to a real key");
                logger.LogError("LogDB exporter is enabled but API key is a placeholder value");
            }
        }
        else
        {
            // Don't add a persistent warning here - the disabled state can be toggled
            // at runtime via the UI, and the dashboard renders a live alert based on
            // current state. Keeping a startup-time warning would go stale.
            logger.LogInformation("LogDB exporter is disabled at startup - logs will be spooled locally until enabled");
        }

        // Validate targets
        if (targets.Targets.Count == 0)
        {
            _agentStatus.AddError("No Nginx targets configured");
            logger.LogError("No Nginx targets configured");
        }
        else if (!targets.Targets.Any(t => t.Enabled))
        {
            _agentStatus.AddError("All Nginx targets are disabled - no logs will be collected");
            logger.LogError("All Nginx targets are disabled - no logs will be collected");
        }

        var enabledTargets = targets.Targets.Where(t => t.Enabled).ToList();
        foreach (var target in enabledTargets)
        {
            if (!string.IsNullOrEmpty(target.AccessLogPath) && !File.Exists(target.AccessLogPath))
            {
                _agentStatus.AddWarning($"Target '{target.Name}': access log not found at {target.AccessLogPath}");
                logger.LogWarning("Target '{Name}': access log not found at {Path}", target.Name, target.AccessLogPath);
            }
            if (!string.IsNullOrEmpty(target.ErrorLogPath) && !File.Exists(target.ErrorLogPath))
            {
                _agentStatus.AddWarning($"Target '{target.Name}': error log not found at {target.ErrorLogPath}");
                logger.LogWarning("Target '{Name}': error log not found at {Path}", target.Name, target.ErrorLogPath);
            }
        }

        // Update target info
        var activeFiles = enabledTargets
            .SelectMany(t => new[] { t.AccessLogPath, t.ErrorLogPath })
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Count();
        _agentStatus.SetTargetInfo(enabledTargets.Count, activeFiles);

        // Validate checkpoint directory
        var checkpointDir = Path.GetDirectoryName(Path.GetFullPath(checkpoint.FilePath));
        if (checkpointDir is not null)
        {
            try
            {
                Directory.CreateDirectory(checkpointDir);
                TestDirectoryWritable(checkpointDir, "Checkpoint", logger);
            }
            catch (Exception ex)
            {
                _agentStatus.AddError($"Cannot create checkpoint directory: {ex.Message}");
                logger.LogError(ex, "Cannot create checkpoint directory");
            }
        }

        // Validate spool directory
        try
        {
            Directory.CreateDirectory(spool.DirectoryPath);
            TestDirectoryWritable(spool.DirectoryPath, "Spool", logger);
        }
        catch (Exception ex)
        {
            _agentStatus.AddError($"Cannot create spool directory: {ex.Message}");
            logger.LogError(ex, "Cannot create spool directory");
        }

        _agentStatus.SetConfigLoaded();
        _agentStatus.SetState(_agentStatus.GetStatus().Errors.Count > 0 ? "degraded" : "running");

        logger.LogInformation("Startup validation complete: {State}", _agentStatus.GetStatus().State);
    }

    private static readonly HashSet<string> PlaceholderApiKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "your-api-key-here",
        "your-api-key",
        "your-key",
        "YOUR_API_KEY_HERE",
        "YOUR_API_KEY",
        "<api-key>",
        "REPLACE_ME"
    };

    private static bool IsPlaceholderApiKey(string apiKey) =>
        PlaceholderApiKeys.Contains(apiKey.Trim());

    private void TestDirectoryWritable(string path, string label, ILogger logger)
    {
        var testFile = Path.Combine(path, $".logdb-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
        }
        catch (Exception ex)
        {
            _agentStatus.AddError($"{label} directory is not writable: {path}");
            logger.LogError(ex, "{Label} directory is not writable: {Path}", label, path);
        }
    }
}
