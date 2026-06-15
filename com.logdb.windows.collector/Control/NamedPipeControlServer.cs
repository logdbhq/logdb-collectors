using System.IO.Pipes;
using System.Text.Json;
using com.logdb.windows.collector.Activity;
using com.logdb.windows.collector.Diagnostics;
using com.logdb.windows.collector.Health;
using com.logdb.windows.collector.Hosting;
using com.logdb.windows.collector.Services;
using com.logdb.windows.collector.Services.Firewall;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.shared.Services;
using Microsoft.Extensions.Options;

namespace com.logdb.windows.collector.Control;

public sealed class NamedPipeControlServer : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly CollectorRuntimeContext _runtimeContext;
    private readonly CollectorStatusRegistry _statusRegistry;
    private readonly CollectorLogSink _logSink;
    private readonly IOptionsMonitor<CollectorConfigDto> _configMonitor;
    private readonly ILogDbConnectionTester _connectionTester;
    private readonly IRuntimeEndpointStore _endpointStore;
    private readonly ICollectorControlInspector _inspector;
    private readonly FirewallSyncEngine _firewallEngine;
    private readonly IHostApplicationLifetime _hostLifetime;
    private readonly IConfiguration _configuration;
    private readonly SendActivityTracker _sendActivity;
    private readonly RecentRecordsBuffer _recentRecords;
    private readonly SemaphoreSlim _configGate = new(1, 1);
    private readonly ILogger<NamedPipeControlServer> _logger;

    public NamedPipeControlServer(
        CollectorRuntimeContext runtimeContext,
        CollectorStatusRegistry statusRegistry,
        CollectorLogSink logSink,
        IOptionsMonitor<CollectorConfigDto> configMonitor,
        ILogDbConnectionTester connectionTester,
        IRuntimeEndpointStore endpointStore,
        ICollectorControlInspector inspector,
        FirewallSyncEngine firewallEngine,
        IHostApplicationLifetime hostLifetime,
        IConfiguration configuration,
        SendActivityTracker sendActivity,
        RecentRecordsBuffer recentRecords,
        ILogger<NamedPipeControlServer> logger)
    {
        _runtimeContext = runtimeContext;
        _statusRegistry = statusRegistry;
        _logSink = logSink;
        _configMonitor = configMonitor;
        _connectionTester = connectionTester;
        _endpointStore = endpointStore;
        _inspector = inspector;
        _firewallEngine = firewallEngine;
        _hostLifetime = hostLifetime;
        _configuration = configuration;
        _sendActivity = sendActivity;
        _recentRecords = recentRecords;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Named pipe control server listening on {PipeName}", _runtimeContext.ControlPipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;

            try
            {
                server = CreatePipeServer();
                await server.WaitForConnectionAsync(stoppingToken);
                _ = HandleClientAsync(server, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                _logger.LogError(ex, "Named pipe server error");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        await using var stream = server;
        using var reader = new StreamReader(stream);
        await using var writer = new StreamWriter(stream) { AutoFlush = true };

        var requestLine = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return;
        }

        ControlResponseDto response;
        try
        {
            var request = JsonSerializer.Deserialize<ControlRequestDto>(requestLine, JsonOptions);
            if (request == null)
            {
                response = new ControlResponseDto { Success = false, Message = "Invalid request payload." };
            }
            else
            {
                response = await ProcessRequestAsync(request, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            response = new ControlResponseDto
            {
                Success = false,
                Message = $"Request processing failed: {ex.Message}"
            };
            _logger.LogError(ex, "Control request failed");
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
    }

    private async Task<ControlResponseDto> ProcessRequestAsync(
        ControlRequestDto request,
        CancellationToken cancellationToken)
    {
        switch (request.Command)
        {
            case ControlCommands.GetStatus:
            {
                // The registry's SentCount only counts module-start cycles (always 1
                // for a running module). Surface the real records-shipped totals from
                // the send-activity tracker instead. Modules that don't ship via the
                // log client (e.g. Firewall) have no entry → 0.
                var snapshot = _statusRegistry.Snapshot();
                var totals = _sendActivity.GetTotalsByModule();
                foreach (var module in snapshot.Modules)
                {
                    if (totals.TryGetValue(module.Name, out var t))
                    {
                        // Records that shipped vs records that failed to ship — so
                        // "Sent 0 / Failed 349" reads as "delivery is failing" rather
                        // than the misleading "0 / 0".
                        module.SentCount = t.Sent;
                        module.FailedCount = t.Failed;
                    }
                    else
                    {
                        // Module ships nothing via the log client (e.g. Firewall) —
                        // leave its registry FailedCount (module-cycle errors) intact.
                        module.SentCount = 0;
                    }
                }
                return new ControlResponseDto
                {
                    Success = true,
                    PayloadJson = JsonSerializer.Serialize(snapshot, JsonOptions)
                };
            }

            case ControlCommands.GetConfig:
            case "redacted-config":
                var redacted = CollectorConfigRedactor.CreateRedacted(_configMonitor.CurrentValue);
                return new ControlResponseDto
                {
                    Success = true,
                    PayloadJson = JsonSerializer.Serialize(redacted, JsonOptions)
                };

            case ControlCommands.UpdateConfig:
                return await UpdateConfigAsync(request.PayloadJson, cancellationToken);

            case ControlCommands.ReloadConfig:
                ReloadConfiguration();
                return new ControlResponseDto
                {
                    Success = true,
                    Message = "Configuration reloaded."
                };

            case ControlCommands.EnableModule:
                return await ToggleModuleAsync(request.PayloadJson, enabled: true, cancellationToken);

            case ControlCommands.DisableModule:
                return await ToggleModuleAsync(request.PayloadJson, enabled: false, cancellationToken);

            case ControlCommands.GetDiagnostics:
                var max = ParseMaxDiagnostics(request.PayloadJson);
                var diagnostics = _logSink.GetRecent(max);
                return new ControlResponseDto
                {
                    Success = true,
                    PayloadJson = JsonSerializer.Serialize(diagnostics, JsonOptions)
                };

            case ControlCommands.GetFailures:
                var maxFailures = ParseMaxDiagnostics(request.PayloadJson);
                var failures = _statusRegistry.RecentFailures(maxFailures);
                return new ControlResponseDto
                {
                    Success = true,
                    PayloadJson = JsonSerializer.Serialize(failures, JsonOptions)
                };

            case ControlCommands.TestConnection:
                var connectionResult = await _connectionTester.TestAsync(_configMonitor.CurrentValue, cancellationToken);
                return new ControlResponseDto
                {
                    Success = connectionResult.Success,
                    Message = connectionResult.Message
                };

            case ControlCommands.ValidateEventLogAccess:
                var eventValidation = await _inspector.ValidateEventLogAccessAsync(_configMonitor.CurrentValue, cancellationToken);
                return new ControlResponseDto
                {
                    Success = true,
                    Message = eventValidation.Message,
                    PayloadJson = JsonSerializer.Serialize(eventValidation, JsonOptions)
                };

            case ControlCommands.ValidateIisPaths:
                var iisValidation = await _inspector.ValidateIisPathsAsync(_configMonitor.CurrentValue, cancellationToken);
                return new ControlResponseDto
                {
                    Success = true,
                    Message = iisValidation.Message,
                    PayloadJson = JsonSerializer.Serialize(iisValidation, JsonOptions)
                };

            case ControlCommands.ValidateDestinationConnection:
                var destinationValidation = await _inspector.ValidateDestinationConnectionAsync(_configMonitor.CurrentValue, cancellationToken);
                return new ControlResponseDto
                {
                    Success = true,
                    Message = destinationValidation.Message,
                    PayloadJson = JsonSerializer.Serialize(destinationValidation, JsonOptions)
                };

            case ControlCommands.PreviewEventLogs:
                var eventPreview = await _inspector.PreviewEventLogsAsync(_configMonitor.CurrentValue, ParsePreviewMax(request.PayloadJson), cancellationToken);
                return new ControlResponseDto
                {
                    Success = true,
                    Message = eventPreview.Message,
                    PayloadJson = JsonSerializer.Serialize(eventPreview, JsonOptions)
                };

            case ControlCommands.PreviewIisLogs:
                var iisPreview = await _inspector.PreviewIisLogsAsync(_configMonitor.CurrentValue, ParsePreviewMax(request.PayloadJson), cancellationToken);
                return new ControlResponseDto
                {
                    Success = true,
                    Message = iisPreview.Message,
                    PayloadJson = JsonSerializer.Serialize(iisPreview, JsonOptions)
                };

            case ControlCommands.PreviewMetrics:
                var metricsPreview = await _inspector.PreviewMetricsAsync(_configMonitor.CurrentValue, cancellationToken);
                return new ControlResponseDto
                {
                    Success = true,
                    Message = metricsPreview.Message,
                    PayloadJson = JsonSerializer.Serialize(metricsPreview, JsonOptions)
                };

            case ControlCommands.GetSendActivity:
                try
                {
                    var query = string.IsNullOrWhiteSpace(request.PayloadJson)
                        ? new SendActivityQueryDto
                        {
                            FromUtc = DateTime.UtcNow.AddDays(-7),
                            ToUtc = DateTime.UtcNow
                        }
                        : JsonSerializer.Deserialize<SendActivityQueryDto>(request.PayloadJson, JsonOptions)
                          ?? new SendActivityQueryDto();
                    var activity = _sendActivity.GetActivity(query);
                    return new ControlResponseDto
                    {
                        Success = true,
                        PayloadJson = JsonSerializer.Serialize(activity, JsonOptions)
                    };
                }
                catch (Exception ex)
                {
                    return new ControlResponseDto
                    {
                        Success = false,
                        Message = $"Send-activity query failed: {ex.GetType().Name}: {ex.Message}"
                    };
                }

            case ControlCommands.ResetSendActivity:
                try
                {
                    _sendActivity.Reset();
                    return new ControlResponseDto { Success = true, Message = "Send statistics cleared." };
                }
                catch (Exception ex)
                {
                    return new ControlResponseDto
                    {
                        Success = false,
                        Message = $"Reset send-activity failed: {ex.GetType().Name}: {ex.Message}"
                    };
                }

            case ControlCommands.GetRecentRecords:
            {
                var maxRecords = ParseMaxDiagnostics(request.PayloadJson);
                var records = _recentRecords.GetRecent(maxRecords);
                return new ControlResponseDto
                {
                    Success = true,
                    PayloadJson = JsonSerializer.Serialize(records, JsonOptions)
                };
            }

            case ControlCommands.GetResolvedEndpoint:
                try
                {
                    // Returns the locked-in endpoint from IRuntimeEndpointStore —
                    // the SAME value every running module uses for its gRPC
                    // channel. No fresh discovery call here; the store
                    // re-resolves only when ApiKey / DiscoveryUrl change.
                    var resolved = await _endpointStore.GetEndpointAsync(cancellationToken);
                    return new ControlResponseDto
                    {
                        Success = true,
                        Message = resolved,
                        PayloadJson = JsonSerializer.Serialize(new ResolvedEndpointDto
                        {
                            Endpoint = resolved,
                            ResolvedAtUtc = DateTime.UtcNow
                        }, JsonOptions)
                    };
                }
                catch (Exception ex)
                {
                    return new ControlResponseDto
                    {
                        Success = false,
                        Message = $"Endpoint resolution failed: {ex.GetType().Name}: {ex.Message}"
                    };
                }

            case ControlCommands.ApplyFirewall:
                var applyResult = await _firewallEngine.SyncAsync(_configMonitor.CurrentValue.LogDB, _configMonitor.CurrentValue.Firewall, cancellationToken);
                return new ControlResponseDto
                {
                    Success = applyResult.Success,
                    Message = applyResult.Message
                };

            case ControlCommands.RemoveFirewall:
                var removeResult = await _firewallEngine.RemoveAllAsync(_configMonitor.CurrentValue.Firewall, cancellationToken);
                return new ControlResponseDto
                {
                    Success = removeResult.Success,
                    Message = removeResult.Message
                };

            case ControlCommands.StopHost:
                if (_runtimeContext.Mode == CollectorInstanceMode.Service)
                {
                    return new ControlResponseDto
                    {
                        Success = false,
                        Message = "Use service management to stop the service instance."
                    };
                }

                _ = Task.Run(() => _hostLifetime.StopApplication(), cancellationToken);
                return new ControlResponseDto
                {
                    Success = true,
                    Message = "Console collector shutdown requested."
                };

            default:
                return new ControlResponseDto
                {
                    Success = false,
                    Message = $"Unknown command: {request.Command}"
                };
        }
    }

    private async Task<ControlResponseDto> UpdateConfigAsync(string? payloadJson, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new ControlResponseDto
            {
                Success = false,
                Message = "Missing configuration payload."
            };
        }

        CollectorConfigDto? updatedConfig;
        try
        {
            updatedConfig = JsonSerializer.Deserialize<CollectorConfigDto>(payloadJson, JsonOptions);
        }
        catch (Exception ex)
        {
            return new ControlResponseDto
            {
                Success = false,
                Message = $"Invalid configuration payload: {ex.Message}"
            };
        }

        if (updatedConfig == null)
        {
            return new ControlResponseDto
            {
                Success = false,
                Message = "Configuration payload could not be parsed."
            };
        }

        await _configGate.WaitAsync(cancellationToken);
        try
        {
            await CollectorConfigPersistence.SaveAsync(updatedConfig, _runtimeContext.ConfigPath, cancellationToken);
            ReloadConfiguration();
        }
        finally
        {
            _configGate.Release();
        }

        return new ControlResponseDto
        {
            Success = true,
            Message = "Configuration updated."
        };
    }

    private async Task<ControlResponseDto> ToggleModuleAsync(
        string? payloadJson,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!TryParseModuleName(payloadJson, out var moduleName, out var error))
        {
            return new ControlResponseDto { Success = false, Message = error };
        }

        await _configGate.WaitAsync(cancellationToken);
        try
        {
            var config = await CollectorConfigPersistence.LoadAsync(_runtimeContext.ConfigPath, cancellationToken);
            if (!TrySetModuleEnabled(config, moduleName, enabled, out var normalizedName, out var moduleError))
            {
                return new ControlResponseDto { Success = false, Message = moduleError };
            }

            await CollectorConfigPersistence.SaveAsync(config, _runtimeContext.ConfigPath, cancellationToken);
            ReloadConfiguration();

            return new ControlResponseDto
            {
                Success = true,
                Message = $"{normalizedName} module {(enabled ? "enabled" : "disabled")}."
            };
        }
        finally
        {
            _configGate.Release();
        }
    }

    private void ReloadConfiguration()
    {
        if (_configuration is IConfigurationRoot root)
        {
            root.Reload();
            return;
        }

        _logger.LogWarning("Configuration reload requested but IConfiguration is not a reloadable root.");
    }

    private static bool TrySetModuleEnabled(
        CollectorConfigDto config,
        string moduleName,
        bool enabled,
        out string normalizedName,
        out string error)
    {
        var key = moduleName.Trim().ToLowerInvariant();
        normalizedName = moduleName.Trim();
        error = string.Empty;

        switch (key)
        {
            case "eventlog":
            case "event-log":
            case "windows-event-log":
                normalizedName = "EventLog";
                config.Modules.EventLog.Enabled = enabled;
                return true;

            case "iis":
            case "iislog":
            case "iis-log":
                normalizedName = "IIS";
                config.Modules.IIS.Enabled = enabled;
                return true;

            case "metrics":
            case "windows-metrics":
                normalizedName = "Metrics";
                config.Modules.Metrics.Enabled = enabled;
                return true;

            case "firewall":
                normalizedName = "Firewall";
                config.Firewall.Enabled = enabled;
                return true;

            case "heartbeat":
                normalizedName = "Heartbeat";
                config.Modules.Heartbeat.Enabled = enabled;
                return true;

            default:
                error = $"Unknown module '{moduleName}'. Valid modules: EventLog, IIS, Metrics, Firewall, Heartbeat.";
                return false;
        }
    }

    private static bool TryParseModuleName(string? payloadJson, out string moduleName, out string error)
    {
        moduleName = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            error = "Module name payload is required.";
            return false;
        }

        if (!payloadJson.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            if (payloadJson.TrimStart().StartsWith("\"", StringComparison.Ordinal))
            {
                try
                {
                    moduleName = JsonSerializer.Deserialize<string>(payloadJson, JsonOptions)?.Trim() ?? string.Empty;
                    return !string.IsNullOrWhiteSpace(moduleName) || SetError("Module name payload is empty.", out error);
                }
                catch (Exception ex)
                {
                    error = $"Invalid module payload: {ex.Message}";
                    return false;
                }
            }

            moduleName = payloadJson.Trim();
            return !string.IsNullOrWhiteSpace(moduleName) || SetError("Module name payload is empty.", out error);
        }

        try
        {
            var request = JsonSerializer.Deserialize<ModuleToggleRequestDto>(payloadJson, JsonOptions);
            moduleName = request?.ModuleName?.Trim() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(moduleName) || SetError("Module name payload is empty.", out error);
        }
        catch (Exception ex)
        {
            error = $"Invalid module payload: {ex.Message}";
            return false;
        }
    }

    private static bool SetError(string message, out string error)
    {
        error = message;
        return false;
    }

    private static int ParseMaxDiagnostics(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return 100;
        }

        if (int.TryParse(payloadJson, out var direct))
        {
            return Math.Clamp(direct, 1, 500);
        }

        try
        {
            var node = JsonSerializer.Deserialize<JsonElement>(payloadJson);
            if (node.ValueKind == JsonValueKind.Object
                && node.TryGetProperty("max", out var maxNode)
                && maxNode.TryGetInt32(out var value))
            {
                return Math.Clamp(value, 1, 500);
            }
        }
        catch
        {
            // ignore invalid payload and use default
        }

        return 100;
    }

    private static int ParsePreviewMax(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return 20;
        }

        if (int.TryParse(payloadJson, out var direct))
        {
            return Math.Clamp(direct, 1, 50);
        }

        try
        {
            var request = JsonSerializer.Deserialize<PreviewRequestDto>(payloadJson, JsonOptions);
            if (request != null)
            {
                return Math.Clamp(request.Max, 1, 50);
            }
        }
        catch
        {
            // ignore parse failures and use default
        }

        return 20;
    }

    private NamedPipeServerStream CreatePipeServer()
    {
        return new NamedPipeServerStream(
            _runtimeContext.ControlPipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 8192,
            outBufferSize: 8192);
    }
}
