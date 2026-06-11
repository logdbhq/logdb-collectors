namespace com.logdb.windows.collector.shared.Contracts;

public enum CollectorInstanceMode
{
    Service = 0,
    Console = 1
}

public class ControlRequestDto
{
    public string Command { get; set; } = string.Empty;
    public string? PayloadJson { get; set; }
}

public class ControlResponseDto
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? PayloadJson { get; set; }
}

public static class ControlCommands
{
    public const string GetStatus = "status";
    public const string GetConfig = "get-config";
    public const string UpdateConfig = "update-config";
    public const string ReloadConfig = "reload-config";
    public const string EnableModule = "enable-module";
    public const string DisableModule = "disable-module";
    public const string GetDiagnostics = "diagnostics";
    public const string GetRecentDiagnostics = GetDiagnostics;
    public const string GetFailures = "failures";
    public const string TestConnection = "test-connection";
    public const string ApplyFirewall = "apply-firewall";
    public const string RemoveFirewall = "remove-firewall";
    public const string StopHost = "stop-host";
    public const string ValidateEventLogAccess = "validate-event-log-access";
    public const string ValidateIisPaths = "validate-iis-paths";
    public const string ValidateDestinationConnection = "validate-destination-connection";
    public const string PreviewEventLogs = "preview-event-logs";
    public const string PreviewIisLogs = "preview-iis-logs";
    public const string PreviewMetrics = "preview-metrics";
    public const string GetResolvedEndpoint = "get-resolved-endpoint";
    public const string GetSendActivity = "send-activity";

    public const string ApplyFirewallRules = ApplyFirewall;
}

/// <summary>
/// Payload returned by ControlCommands.GetResolvedEndpoint — lets the UI ask the
/// service "what gRPC endpoint are you currently using?" instead of doing its own
/// discovery call. Same endpoint = Test is guaranteed identical to production.
/// </summary>
public sealed class ResolvedEndpointDto
{
    public string Endpoint { get; set; } = string.Empty;
    public DateTime ResolvedAtUtc { get; set; }
}

public static class ControlChannelDefaults
{
    public const string ServicePipeName = "com.logdb.windows.collector.service";
    public const string ConsolePipeName = "com.logdb.windows.collector.console";
    public const string LegacyPipeName = "LogDB.Windows.Collector.Control";

    public const string LegacyPipeEnvironmentVariable = "LOGDB_COLLECTOR_PIPE_NAME";
    public const string ServicePipeEnvironmentVariable = "LOGDB_COLLECTOR_SERVICE_PIPE_NAME";
    public const string ConsolePipeEnvironmentVariable = "LOGDB_COLLECTOR_CONSOLE_PIPE_NAME";

    public static string ResolvePipeName(CollectorInstanceMode mode)
    {
        var legacy = Environment.GetEnvironmentVariable(LegacyPipeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(legacy))
        {
            return legacy;
        }

        var specificName = mode == CollectorInstanceMode.Service
            ? Environment.GetEnvironmentVariable(ServicePipeEnvironmentVariable)
            : Environment.GetEnvironmentVariable(ConsolePipeEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(specificName))
        {
            return specificName;
        }

        return mode == CollectorInstanceMode.Service
            ? ServicePipeName
            : ConsolePipeName;
    }

    public static string ResolvePipeName()
    {
        return ResolvePipeName(CollectorInstanceMode.Service);
    }
}

public sealed class ModuleToggleRequestDto
{
    public string ModuleName { get; set; } = string.Empty;
}
