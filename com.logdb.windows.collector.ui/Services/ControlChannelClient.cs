using System.IO.Pipes;
using System.Text.Json;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.shared.Services;

namespace com.logdb.windows.collector.ui.Services;

public sealed class ControlChannelClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public Task<ControlResponseDto> SendAsync(
        CollectorInstanceMode mode,
        string command,
        string? payloadJson = null,
        int timeoutMilliseconds = 5000,
        CancellationToken cancellationToken = default)
    {
        return SendToPipeAsync(
            CollectorInstanceDiscovery.ResolvePipeName(mode),
            command,
            payloadJson,
            timeoutMilliseconds,
            cancellationToken);
    }

    public async Task<ControlResponseDto> SendToPipeAsync(
        string pipeName,
        string command,
        string? payloadJson = null,
        int timeoutMilliseconds = 5000,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMilliseconds);

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync(timeoutCts.Token);

            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };

            var request = new ControlRequestDto
            {
                Command = command,
                PayloadJson = payloadJson
            };

            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
            var responseLine = await reader.ReadLineAsync(timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                return new ControlResponseDto
                {
                    Success = false,
                    Message = "Collector returned an empty response."
                };
            }

            var response = JsonSerializer.Deserialize<ControlResponseDto>(responseLine, JsonOptions);
            return response ?? new ControlResponseDto
            {
                Success = false,
                Message = "Failed to parse collector response."
            };
        }
        catch (Exception ex)
        {
            return new ControlResponseDto
            {
                Success = false,
                Message = ex.Message
            };
        }
    }

    public async Task<bool> IsEndpointAvailableAsync(
        CollectorInstanceMode mode,
        int timeoutMilliseconds = 800,
        CancellationToken cancellationToken = default)
    {
        var status = await SendAsync(
            mode,
            ControlCommands.GetStatus,
            timeoutMilliseconds: timeoutMilliseconds,
            cancellationToken: cancellationToken);
        return status.Success;
    }

    public async Task<CollectorStatusDto?> GetStatusAsync(
        CollectorInstanceMode mode,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(mode, ControlCommands.GetStatus, cancellationToken: cancellationToken);
        if (!response.Success || string.IsNullOrWhiteSpace(response.PayloadJson))
        {
            return null;
        }

        return JsonSerializer.Deserialize<CollectorStatusDto>(response.PayloadJson, JsonOptions);
    }

    public async Task<IReadOnlyList<DiagnosticEntryDto>> GetDiagnosticsAsync(
        CollectorInstanceMode mode,
        int maxEntries = 100,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            mode,
            ControlCommands.GetDiagnostics,
            payloadJson: maxEntries.ToString(),
            cancellationToken: cancellationToken);

        if (!response.Success || string.IsNullOrWhiteSpace(response.PayloadJson))
        {
            return Array.Empty<DiagnosticEntryDto>();
        }

        var entries = JsonSerializer.Deserialize<List<DiagnosticEntryDto>>(response.PayloadJson, JsonOptions);
        return entries ?? new List<DiagnosticEntryDto>();
    }

    public async Task<IReadOnlyList<CollectorFailureDto>> GetFailuresAsync(
        CollectorInstanceMode mode,
        int maxEntries = 250,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            mode,
            ControlCommands.GetFailures,
            payloadJson: maxEntries.ToString(),
            cancellationToken: cancellationToken);

        if (!response.Success || string.IsNullOrWhiteSpace(response.PayloadJson))
        {
            return Array.Empty<CollectorFailureDto>();
        }

        var failures = JsonSerializer.Deserialize<List<CollectorFailureDto>>(response.PayloadJson, JsonOptions);
        return failures ?? new List<CollectorFailureDto>();
    }

    /// <summary>
    /// Returns null (not empty) when the target collector predates the
    /// firewall-history command, so callers can fall back to diagnostics
    /// scraping instead of showing a blank history.
    /// </summary>
    public async Task<IReadOnlyList<FirewallRuleHistoryEntryDto>?> GetFirewallHistoryAsync(
        CollectorInstanceMode mode,
        int maxEntries = 100,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            mode,
            ControlCommands.GetFirewallHistory,
            payloadJson: maxEntries.ToString(),
            cancellationToken: cancellationToken);

        if (!response.Success || string.IsNullOrWhiteSpace(response.PayloadJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<FirewallRuleHistoryEntryDto>>(response.PayloadJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rules == null means the call failed; then Unsupported distinguishes
    /// "old service without the firewall-rules command" from a real error
    /// (timeout, busy service, parse failure) whose text is in Error — the
    /// two used to be conflated, telling operators on a current service to
    /// "update the service" when the call had merely timed out. The 90 s
    /// timeout is deliberate: listing queries the address filters of
    /// 5000-IP rules, which is slow while a sync is rewriting them.
    /// </summary>
    public async Task<(IReadOnlyList<FirewallRuleInfoDto>? Rules, string Error, bool Unsupported)> GetFirewallRulesAsync(
        CollectorInstanceMode mode,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            mode,
            ControlCommands.GetFirewallRules,
            timeoutMilliseconds: 90000,
            cancellationToken: cancellationToken);

        if (!response.Success || string.IsNullOrWhiteSpace(response.PayloadJson))
        {
            var message = string.IsNullOrWhiteSpace(response.Message) ? "The collector returned no response." : response.Message;
            var unsupported = message.Contains("Unknown command", StringComparison.OrdinalIgnoreCase);
            if (message.Contains("canceled", StringComparison.OrdinalIgnoreCase))
            {
                message = "timed out — the service may be busy applying rules right now; try again shortly";
            }
            return (null, message, unsupported);
        }

        try
        {
            var rules = JsonSerializer.Deserialize<List<FirewallRuleInfoDto>>(response.PayloadJson, JsonOptions);
            return rules == null
                ? (null, "Failed to parse the rule listing payload.", false)
                : (rules, string.Empty, false);
        }
        catch (JsonException)
        {
            return (null, "Failed to parse the rule listing payload.", false);
        }
    }

    public async Task<(bool Success, string Message)> DeleteFirewallRuleAsync(
        CollectorInstanceMode mode,
        string ruleId,
        bool removeFromBackend,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new DeleteFirewallRuleRequestDto
        {
            RuleId = ruleId,
            RemoveFromBackend = removeFromBackend
        }, JsonOptions);

        var response = await SendAsync(
            mode,
            ControlCommands.DeleteFirewallRule,
            payloadJson: payload,
            timeoutMilliseconds: 30000,
            cancellationToken: cancellationToken);

        return (response.Success, response.Message ?? (response.Success ? "Rule deleted." : "Delete failed."));
    }

    /// <summary>Null when the target collector predates the blocked-ips index.</summary>
    public async Task<BlockedIpListResponseDto?> GetFirewallBlockedIpsAsync(
        CollectorInstanceMode mode,
        string filter,
        int max = 500,
        string? source = null,
        string? kind = null,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new BlockedIpQueryDto
        {
            Filter = filter,
            Max = max,
            Source = source ?? string.Empty,
            Kind = string.IsNullOrWhiteSpace(kind) ? BlockedIpKinds.All : kind
        }, JsonOptions);
        var response = await SendAsync(
            mode,
            ControlCommands.GetFirewallBlockedIps,
            payloadJson: payload,
            timeoutMilliseconds: 30000,
            cancellationToken: cancellationToken);

        if (!response.Success || string.IsNullOrWhiteSpace(response.PayloadJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BlockedIpListResponseDto>(response.PayloadJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<(bool Success, string Message, FirewallRuleIpsDto? Rule)> GetFirewallRuleIpsAsync(
        CollectorInstanceMode mode,
        string ruleId,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            mode,
            ControlCommands.GetFirewallRuleIps,
            payloadJson: JsonSerializer.Serialize(ruleId, JsonOptions),
            timeoutMilliseconds: 30000,
            cancellationToken: cancellationToken);

        if (!response.Success || string.IsNullOrWhiteSpace(response.PayloadJson))
        {
            return (false, response.Message ?? "Failed to load rule IPs (older collector version?).", null);
        }

        try
        {
            var rule = JsonSerializer.Deserialize<FirewallRuleIpsDto>(response.PayloadJson, JsonOptions);
            return rule == null
                ? (false, "Failed to parse rule IPs payload.", null)
                : (true, string.Empty, rule);
        }
        catch (JsonException)
        {
            return (false, "Failed to parse rule IPs payload.", null);
        }
    }

    public async Task<CollectorConfigDto?> GetRedactedConfigAsync(
        CollectorInstanceMode mode,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(mode, ControlCommands.GetConfig, cancellationToken: cancellationToken);
        if (!response.Success || string.IsNullOrWhiteSpace(response.PayloadJson))
        {
            return null;
        }

        return JsonSerializer.Deserialize<CollectorConfigDto>(response.PayloadJson, JsonOptions);
    }

    public async Task<IReadOnlyList<RecentRecordDto>> GetRecentRecordsAsync(
        CollectorInstanceMode mode,
        int maxEntries = 200,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            mode,
            ControlCommands.GetRecentRecords,
            payloadJson: maxEntries.ToString(),
            cancellationToken: cancellationToken);

        if (!response.Success || string.IsNullOrWhiteSpace(response.PayloadJson))
        {
            return Array.Empty<RecentRecordDto>();
        }

        var records = JsonSerializer.Deserialize<List<RecentRecordDto>>(response.PayloadJson, JsonOptions);
        return records ?? new List<RecentRecordDto>();
    }

    public async Task<SendActivityDto?> GetSendActivityAsync(
        CollectorInstanceMode mode,
        SendActivityQueryDto query,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            mode,
            ControlCommands.GetSendActivity,
            payloadJson: JsonSerializer.Serialize(query, JsonOptions),
            cancellationToken: cancellationToken);

        if (!response.Success || string.IsNullOrWhiteSpace(response.PayloadJson))
        {
            return null;
        }

        return JsonSerializer.Deserialize<SendActivityDto>(response.PayloadJson, JsonOptions);
    }
}
