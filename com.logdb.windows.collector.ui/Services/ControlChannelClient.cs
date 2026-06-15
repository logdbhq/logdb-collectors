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
