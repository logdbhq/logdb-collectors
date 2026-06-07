using System.Text.Json;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.shared.Services;
using com.logdb.windows.collector.ui.ViewModels;

namespace com.logdb.windows.collector.ui.Services;

public sealed class LocalCollectorAdminClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ControlChannelClient _controlClient;
    private readonly CollectorDiscoveryService _discoveryService;
    private readonly ConsoleCollectorControl _consoleCollectorControl;
    private readonly CollectorServiceUpdateService _serviceUpdateService;
    private readonly string _configPath;

    private CollectorConfigDto _workingConfig = new();
    private string _apiKeySecret = string.Empty;
    private string _collectorExeOverride = string.Empty;

    public LocalCollectorAdminClient()
        : this(new ControlChannelClient())
    {
    }

    public LocalCollectorAdminClient(ControlChannelClient controlChannelClient)
    {
        _controlClient = controlChannelClient;
        _discoveryService = new CollectorDiscoveryService(controlChannelClient);
        _consoleCollectorControl = new ConsoleCollectorControl(controlChannelClient);
        _serviceUpdateService = new CollectorServiceUpdateService();
        _configPath = CollectorPathDefaults.ConfigPath;
    }

    public CollectorDiscoverySnapshot? Discovery { get; private set; }
    public CollectorInstanceMode? SelectedTarget { get; private set; }
    public string ConfigPath => _configPath;
    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKeySecret);

    public string CollectorExeOverride
    {
        get => _collectorExeOverride;
        set => _collectorExeOverride = value?.Trim() ?? string.Empty;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await CollectorConfigPersistence.EnsureExistsAsync(_configPath, cancellationToken);
        _workingConfig = await CollectorConfigPersistence.LoadAsync(_configPath, cancellationToken);
        _apiKeySecret = _workingConfig.LogDB.ApiKey;
        await LoadUiSettingsAsync(cancellationToken);
        await RefreshDiscoveryAsync(cancellationToken);
    }

    public async Task SaveApiKeyToDiskAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        _apiKeySecret = apiKey.Trim();
        _workingConfig.LogDB.ApiKey = _apiKeySecret;
        await CollectorConfigPersistence.SaveAsync(_workingConfig, _configPath, cancellationToken);
    }

    public CollectorConfigDto SnapshotWorkingConfig()
    {
        return CloneConfig(_workingConfig);
    }

    public void SetSelectedTarget(CollectorInstanceMode? mode)
    {
        SelectedTarget = mode;
    }

    public async Task RefreshDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        Discovery = await _discoveryService.DiscoverAsync(cancellationToken);

        var availableModes = GetAvailableTargets().ToList();
        if (SelectedTarget != null && availableModes.Contains(SelectedTarget.Value))
        {
            return;
        }

        if (availableModes.Contains(CollectorInstanceMode.Service))
        {
            SelectedTarget = CollectorInstanceMode.Service;
            return;
        }

        if (availableModes.Contains(CollectorInstanceMode.Console))
        {
            SelectedTarget = CollectorInstanceMode.Console;
            return;
        }

        SelectedTarget = null;
    }

    public IEnumerable<CollectorInstanceMode> GetAvailableTargets()
    {
        if (Discovery?.ServiceEndpoint.IsReachable == true)
        {
            yield return CollectorInstanceMode.Service;
        }

        if (Discovery?.ConsoleEndpoint.IsReachable == true)
        {
            yield return CollectorInstanceMode.Console;
        }
    }

    public async Task<CollectorStatusDto?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedTarget == null)
        {
            return null;
        }

        return await _controlClient.GetStatusAsync(SelectedTarget.Value, cancellationToken);
    }

    public async Task<IReadOnlyList<DiagnosticEntryDto>> GetDiagnosticsAsync(
        int maxEntries = 200,
        CancellationToken cancellationToken = default)
    {
        if (SelectedTarget == null)
        {
            return Array.Empty<DiagnosticEntryDto>();
        }

        return await _controlClient.GetDiagnosticsAsync(SelectedTarget.Value, maxEntries, cancellationToken);
    }

    public async Task<IReadOnlyList<CollectorFailureDto>> GetFailuresAsync(
        int maxEntries = 250,
        CancellationToken cancellationToken = default)
    {
        if (SelectedTarget == null)
        {
            return Array.Empty<CollectorFailureDto>();
        }

        return await _controlClient.GetFailuresAsync(SelectedTarget.Value, maxEntries, cancellationToken);
    }

    public async Task<CollectorConfigDto?> GetEffectiveRedactedConfigAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedTarget == null)
        {
            return null;
        }

        return await _controlClient.GetRedactedConfigAsync(SelectedTarget.Value, cancellationToken);
    }

    public async Task<(bool Success, string Message)> ApplyConfigAsync(
        CollectorConfigDto candidate,
        string? replacementApiKey = null,
        CancellationToken cancellationToken = default)
    {
        var fullConfig = CloneConfig(candidate);
        if (!string.IsNullOrWhiteSpace(replacementApiKey))
        {
            _apiKeySecret = replacementApiKey.Trim();
            DebugLog($"ApplyConfigAsync: API key replacement requested (apiKey={Mask(_apiKeySecret)})");
        }

        if (string.IsNullOrWhiteSpace(_apiKeySecret))
        {
            return (false, "API key is required before applying configuration.");
        }

        fullConfig.LogDB.ApiKey = _apiKeySecret;

        // Always persist to disk first — the file is the source of truth and the
        // collector service reads it via IOptionsMonitor whether it's running now
        // or starts up later.
        try
        {
            await CollectorConfigPersistence.SaveAsync(fullConfig, _configPath, cancellationToken);
            _workingConfig = fullConfig;
            DebugLog($"ApplyConfigAsync: saved config to disk ({_configPath})");
        }
        catch (Exception ex)
        {
            DebugLog($"ApplyConfigAsync: disk save FAILED — {ex.GetType().Name}: {ex.Message}");
            return (false, $"Failed to save configuration to disk: {ex.Message}");
        }

        // If no collector instance is running, that's all we can do — the service
        // will pick this up on next start. This is normal for first-time setup or
        // any "service is stopped" state.
        if (SelectedTarget == null)
        {
            DebugLog("ApplyConfigAsync: no SelectedTarget — disk-only save (service will pick up on next start)");
            return (true, "Saved to disk. Collector service is not running — it will pick up the new config when started.");
        }

        DebugLog($"ApplyConfigAsync: pushing live UpdateConfig + ReloadConfig to {SelectedTarget.Value}");

        // A collector instance is running — push the live update + reload command.
        var response = await _controlClient.SendAsync(
            SelectedTarget.Value,
            ControlCommands.UpdateConfig,
            JsonSerializer.Serialize(fullConfig, JsonOptions),
            cancellationToken: cancellationToken);

        if (!response.Success)
        {
            return (false, response.Message ?? "Config saved to disk, but live update to the running collector failed.");
        }

        var reloadResponse = await _controlClient.SendAsync(
            SelectedTarget.Value,
            ControlCommands.ReloadConfig,
            cancellationToken: cancellationToken);

        if (!reloadResponse.Success)
        {
            return (false, reloadResponse.Message ?? "Configuration updated, but reload failed.");
        }

        return (true, "Configuration applied.");
    }

    public async Task<(bool Success, string Message)> ReloadTargetConfigAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedTarget == null)
        {
            return (false, "No local collector target selected.");
        }

        var response = await _controlClient.SendAsync(
            SelectedTarget.Value,
            ControlCommands.ReloadConfig,
            cancellationToken: cancellationToken);

        return (response.Success, response.Message ?? (response.Success ? "Configuration reloaded." : "Reload failed."));
    }

    public async Task<ValidationResultDto> ValidateEventLogAccessAsync(CancellationToken cancellationToken = default)
    {
        return await SendForValidationAsync(ControlCommands.ValidateEventLogAccess, null, cancellationToken);
    }

    public async Task<ValidationResultDto> ValidateIisPathsAsync(CancellationToken cancellationToken = default)
    {
        return await SendForValidationAsync(ControlCommands.ValidateIisPaths, null, cancellationToken);
    }

    public async Task<ValidationResultDto> ValidateDestinationConnectionAsync(CancellationToken cancellationToken = default)
    {
        return await SendForValidationAsync(ControlCommands.ValidateDestinationConnection, null, cancellationToken);
    }

    public async Task<PreviewResultDto<EventLogPreviewRowDto>> PreviewEventLogsAsync(
        int max = 20,
        CancellationToken cancellationToken = default)
    {
        return await SendForPreviewAsync<EventLogPreviewRowDto>(ControlCommands.PreviewEventLogs, max, cancellationToken);
    }

    public async Task<PreviewResultDto<IisPreviewRowDto>> PreviewIisLogsAsync(
        int max = 20,
        CancellationToken cancellationToken = default)
    {
        return await SendForPreviewAsync<IisPreviewRowDto>(ControlCommands.PreviewIisLogs, max, cancellationToken);
    }

    public async Task<PreviewResultDto<MetricPreviewRowDto>> PreviewMetricsAsync(CancellationToken cancellationToken = default)
    {
        return await SendForPreviewAsync<MetricPreviewRowDto>(ControlCommands.PreviewMetrics, 20, cancellationToken);
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var validation = await ValidateDestinationConnectionAsync(cancellationToken);
        return (validation.Success, validation.Message);
    }

    public enum EndpointResolutionSource { RunningService, Discovery, ServiceCache, None }

    /// <summary>
    /// Resolves the gRPC-logger URL for the current API key. Tries discovery first; if
    /// that fails, falls back to the endpoint-cache.json file the collector service
    /// writes after successful resolutions. Source enum tells the caller which path won
    /// so the UI can label the value (live vs cached).
    /// </summary>
    public async Task<EndpointResolution> ResolveGrpcLoggerEndpointAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKeySecret))
        {
            DebugLog("ResolveGrpcLoggerEndpointAsync: no API key, skipping");
            return EndpointResolution.None;
        }

        // PREFERRED PATH: if a collector instance is running, ask it directly.
        // This guarantees Test uses the same endpoint production is using —
        // even when discovery is inconsistent / load-balanced across backends
        // that disagree about the API key's mapping. Production-by-construction.
        if (SelectedTarget != null)
        {
            DebugLog($"ResolveGrpcLoggerEndpointAsync: asking running service {SelectedTarget.Value} via control channel");
            try
            {
                var response = await _controlClient.SendAsync(
                    SelectedTarget.Value,
                    ControlCommands.GetResolvedEndpoint,
                    cancellationToken: cancellationToken);

                if (response.Success && !string.IsNullOrWhiteSpace(response.PayloadJson))
                {
                    var payload = JsonSerializer.Deserialize<ResolvedEndpointDto>(response.PayloadJson, JsonOptions);
                    if (payload is { Endpoint.Length: > 0 })
                    {
                        DebugLog($"ResolveGrpcLoggerEndpointAsync: service returned {payload.Endpoint} (resolved {payload.ResolvedAtUtc:O})");
                        return new EndpointResolution(payload.Endpoint, EndpointResolutionSource.RunningService, payload.ResolvedAtUtc, null, null);
                    }
                }

                DebugLog($"ResolveGrpcLoggerEndpointAsync: service responded but no usable endpoint (success={response.Success}, message={response.Message}) — falling back to local discovery");
            }
            catch (Exception ex)
            {
                DebugLog($"ResolveGrpcLoggerEndpointAsync: control-channel call threw {ex.GetType().Name}: {ex.Message} — falling back to local discovery");
            }
        }

        var discoveryUrl = _workingConfig.LogDB.DiscoveryUrl;
        string? lastError = null;
        int? lastStatusCode = null;

        if (!string.IsNullOrWhiteSpace(discoveryUrl))
        {
            DebugLog($"ResolveGrpcLoggerEndpointAsync: calling {discoveryUrl} (apiKey={Mask(_apiKeySecret)})");
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, discoveryUrl);
                request.Headers.TryAddWithoutValidation("X-API-Key", _apiKeySecret);

                using var response = await http.SendAsync(request, cancellationToken);
                lastStatusCode = (int)response.StatusCode;
                DebugLog($"ResolveGrpcLoggerEndpointAsync: discovery responded {lastStatusCode} {response.ReasonPhrase}");
                if (response.IsSuccessStatusCode)
                {
                    var text = await response.Content.ReadAsStringAsync(cancellationToken);
                    var parsed = ParseDiscoveryBody(text);
                    if (!string.IsNullOrWhiteSpace(parsed))
                    {
                        DebugLog($"ResolveGrpcLoggerEndpointAsync: live OK → {parsed}");
                        return new EndpointResolution(parsed, EndpointResolutionSource.Discovery, DateTime.UtcNow, lastStatusCode, null);
                    }
                    DebugLog("ResolveGrpcLoggerEndpointAsync: discovery returned 2xx but body did not yield a URL");
                    lastError = "2xx with empty body";
                }
                else
                {
                    lastError = $"HTTP {lastStatusCode} {response.ReasonPhrase}";
                }
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException ex)
            {
                lastError = "timeout after 15s";
                DebugLog($"ResolveGrpcLoggerEndpointAsync: timeout — {ex.Message}");
            }
            catch (Exception ex)
            {
                lastError = $"{ex.GetType().Name}: {ex.Message}";
                DebugLog($"ResolveGrpcLoggerEndpointAsync: discovery call threw — {lastError}");
            }
        }
        else
        {
            DebugLog("ResolveGrpcLoggerEndpointAsync: DiscoveryUrl is empty, skipping discovery call");
            lastError = "DiscoveryUrl is empty";
        }

        var cached = TryReadServiceEndpointCache();
        if (cached is { Endpoint: { Length: > 0 } } && CacheMatchesCurrentKey(cached))
        {
            DebugLog($"ResolveGrpcLoggerEndpointAsync: serving cached endpoint {cached.Endpoint} (resolved {cached.ResolvedAtUtc:O})");
            return new EndpointResolution(cached.Endpoint, EndpointResolutionSource.ServiceCache, cached.ResolvedAtUtc, lastStatusCode, lastError);
        }

        DebugLog($"ResolveGrpcLoggerEndpointAsync: no live result, no cached fallback — returning None (lastError={lastError})");
        return new EndpointResolution(null, EndpointResolutionSource.None, null, lastStatusCode, lastError);
    }

    public readonly record struct EndpointResolution(
        string? Endpoint,
        EndpointResolutionSource Source,
        DateTime? ResolvedAtUtc,
        int? LastDiscoveryStatusCode,
        string? LastError)
    {
        public static readonly EndpointResolution None = new(null, EndpointResolutionSource.None, null, null, null);
    }

    private static string? ParseDiscoveryBody(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("serviceUrl", out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var url = prop.GetString();
                if (!string.IsNullOrWhiteSpace(url)) return url.Trim();
            }
        }
        catch { /* not a JSON object */ }

        try
        {
            var fromJson = JsonSerializer.Deserialize<string>(text);
            if (!string.IsNullOrWhiteSpace(fromJson)) return fromJson.Trim();
        }
        catch { /* not a JSON string */ }

        return text.Trim().Trim('"');
    }

    private sealed class ServiceEndpointCache
    {
        public string ApiKeyFingerprint { get; set; } = string.Empty;
        public string DiscoveryUrl { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public DateTime ResolvedAtUtc { get; set; }
    }

    private static ServiceEndpointCache? TryReadServiceEndpointCache()
    {
        try
        {
            var path = CollectorPathDefaults.EndpointCachePath;
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ServiceEndpointCache>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private bool CacheMatchesCurrentKey(ServiceEndpointCache cache)
    {
        // The service stores a SHA256 fingerprint of the key — recompute and compare so
        // we never honor a cache entry written for a different API key.
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(_apiKeySecret ?? string.Empty));
        var fingerprint = Convert.ToHexString(bytes);
        return string.Equals(cache.ApiKeyFingerprint, fingerprint, StringComparison.Ordinal);
    }

    public async Task<(int? AccountId, string? AccountName)> ResolveOwnerAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKeySecret))
        {
            DebugLog("ResolveOwnerAsync: no API key, skipping");
            return (null, null);
        }

        var discoveryUrl = _workingConfig.LogDB.DiscoveryUrl;
        if (string.IsNullOrWhiteSpace(discoveryUrl) || !Uri.TryCreate(discoveryUrl, UriKind.Absolute, out var configuredUri))
        {
            DebugLog($"ResolveOwnerAsync: DiscoveryUrl invalid or empty ({discoveryUrl ?? "<null>"})");
            return (null, null);
        }

        var ownerUrl = new Uri(new Uri($"{configuredUri.Scheme}://{configuredUri.Authority}"), "/resolve/owner");
        DebugLog($"ResolveOwnerAsync: GET {ownerUrl}");

        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, ownerUrl);
            request.Headers.TryAddWithoutValidation("X-API-Key", _apiKeySecret);

            using var response = await http.SendAsync(request, cancellationToken);
            DebugLog($"ResolveOwnerAsync: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            if (!response.IsSuccessStatusCode)
            {
                return (null, null);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            int? accountId = doc.RootElement.TryGetProperty("accountId", out var idProp) && idProp.ValueKind == JsonValueKind.Number
                ? idProp.GetInt32()
                : null;
            string? accountName = doc.RootElement.TryGetProperty("accountName", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                ? nameProp.GetString()
                : null;

            DebugLog($"ResolveOwnerAsync: accountId={accountId}, accountName={accountName ?? "<null>"}");
            return (accountId, accountName);
        }
        catch (Exception ex)
        {
            DebugLog($"ResolveOwnerAsync: threw — {ex.GetType().Name}: {ex.Message}");
            return (null, null);
        }
    }

    private static string Mask(string? apiKey)
    {
        return string.IsNullOrEmpty(apiKey) ? "(none)" : "(configured)";
    }

    private static void DebugLog(string message)
    {
        var line = $"[LogDBUI {DateTime.Now:HH:mm:ss.fff}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        Console.WriteLine(line);
    }

    public async Task<(bool Success, string Message)> InstallServiceAsync(CancellationToken cancellationToken = default)
    {
        var path = ResolveCollectorExecutablePath();
        var result = await ServiceControl.InstallAsync(path);
        await RefreshDiscoveryAsync(cancellationToken);
        return result;
    }

    public async Task<(bool Success, string Message)> UninstallServiceAsync(CancellationToken cancellationToken = default)
    {
        var result = await ServiceControl.UninstallAsync();
        await RefreshDiscoveryAsync(cancellationToken);
        return result;
    }

    public async Task<(bool Success, string Message)> StartServiceAsync(CancellationToken cancellationToken = default)
    {
        var result = await ServiceControl.StartAsync();
        await RefreshDiscoveryAsync(cancellationToken);
        return result;
    }

    public async Task<(bool Success, string Message)> StopServiceAsync(CancellationToken cancellationToken = default)
    {
        var result = await ServiceControl.StopAsync();
        await RefreshDiscoveryAsync(cancellationToken);
        return result;
    }

    public async Task<(bool Success, string Message)> RestartServiceAsync(CancellationToken cancellationToken = default)
    {
        var stop = await ServiceControl.StopAsync();
        if (!stop.Success)
        {
            await RefreshDiscoveryAsync(cancellationToken);
            return stop;
        }

        var start = await ServiceControl.StartAsync();
        await RefreshDiscoveryAsync(cancellationToken);
        return start;
    }

    public async Task<(bool Success, string Message)> RunConsoleInstanceAsync(CancellationToken cancellationToken = default)
    {
        var path = ResolveCollectorExecutablePath();
        var result = await _consoleCollectorControl.StartAsync(path, cancellationToken);
        await RefreshDiscoveryAsync(cancellationToken);
        return result;
    }

    public async Task<(bool Success, string Message)> StopConsoleInstanceAsync(CancellationToken cancellationToken = default)
    {
        var result = await _consoleCollectorControl.StopAsync(cancellationToken);
        await RefreshDiscoveryAsync(cancellationToken);
        return result;
    }

    public async Task<(bool Success, string Message)> EnableModuleAsync(string moduleName, CancellationToken cancellationToken = default)
    {
        if (SelectedTarget == null)
        {
            return (false, "No local collector target selected.");
        }

        var response = await _controlClient.SendAsync(SelectedTarget.Value, ControlCommands.EnableModule, $"\"{moduleName}\"", cancellationToken: cancellationToken);
        return (response.Success, response.Message ?? (response.Success ? $"{moduleName} enabled." : $"Failed to enable {moduleName}."));
    }

    public async Task<(bool Success, string Message)> DisableModuleAsync(string moduleName, CancellationToken cancellationToken = default)
    {
        if (SelectedTarget == null)
        {
            return (false, "No local collector target selected.");
        }

        var response = await _controlClient.SendAsync(SelectedTarget.Value, ControlCommands.DisableModule, $"\"{moduleName}\"", cancellationToken: cancellationToken);
        return (response.Success, response.Message ?? (response.Success ? $"{moduleName} disabled." : $"Failed to disable {moduleName}."));
    }

    public async Task<(bool Success, string Message)> SendTestLogAsync(
        string moduleName,
        IProgress<TestReportStep>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // The Test buttons must work even when no collector instance is running, so we resolve
        // the endpoint here in the UI process and ship the log directly via the SDK rather than
        // routing through the named-pipe control server.
        if (string.IsNullOrWhiteSpace(_apiKeySecret))
        {
            progress?.Report(new TestReportStep("Resolve API key", "no key set (configure on Destination page)", TestReportStepStatus.Fail));
            return (false, "Test log: set the LogDB API key on the Destination page first.");
        }

        progress?.Report(new TestReportStep("Resolve discovery URL",
            string.IsNullOrWhiteSpace(_workingConfig.LogDB.DiscoveryUrl) ? "(empty — will skip discovery)" : _workingConfig.LogDB.DiscoveryUrl!,
            TestReportStepStatus.Info));

        progress?.Report(new TestReportStep("Resolve gRPC endpoint", "calling discovery service…", TestReportStepStatus.Running));
        var resolution = await ResolveGrpcLoggerEndpointAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(resolution.Endpoint))
        {
            var detail = resolution.LastError ?? "no discovery response and no cached endpoint";
            progress?.Report(new TestReportStep("Resolve gRPC endpoint", $"failed — {detail}", TestReportStepStatus.Fail));
            return (false, $"Test log: could not resolve gRPC endpoint ({detail}).");
        }

        progress?.Report(new TestReportStep("Resolve gRPC endpoint",
            $"{resolution.Endpoint} (source={resolution.Source}, statusCode={resolution.LastDiscoveryStatusCode?.ToString() ?? "—"})",
            TestReportStepStatus.Ok));

        DebugLog($"SendTestLogAsync: module={moduleName} resolvedEndpoint={resolution.Endpoint} source={resolution.Source}");

        // Use the current working config plus the freshly resolved endpoint and the secret API key.
        var config = SnapshotWorkingConfig();
        config.LogDB.ApiKey = _apiKeySecret;

        var dispatcher = new UiTestLogDispatcher();
        var result = await dispatcher.SendAsync(moduleName, config, resolution.Endpoint!, progress, cancellationToken);
        DebugLog($"SendTestLogAsync: module={moduleName} success={result.Success} message={result.Message}");
        return result;
    }

    public async Task<(bool Success, string Message)> ApplyFirewallAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedTarget == null)
        {
            return (false, "No local collector target selected.");
        }

        var response = await _controlClient.SendAsync(SelectedTarget.Value, ControlCommands.ApplyFirewallRules, cancellationToken: cancellationToken);
        return (response.Success, response.Message ?? (response.Success ? "Firewall rule applied." : "Failed to apply firewall rule."));
    }

    public async Task<(bool Success, string Message)> RemoveFirewallAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedTarget == null)
        {
            return (false, "No local collector target selected.");
        }

        var response = await _controlClient.SendAsync(SelectedTarget.Value, ControlCommands.RemoveFirewall, cancellationToken: cancellationToken);
        return (response.Success, response.Message ?? (response.Success ? "Firewall rule removed." : "Failed to remove firewall rule."));
    }

    public async Task<ServiceUpdateCheckResult> CheckServiceUpdateAsync(CancellationToken cancellationToken = default)
    {
        return await _serviceUpdateService.CheckAsync(cancellationToken);
    }

    public async Task<(bool Success, string Message)> ApplyServiceUpdateAsync(
        ServiceUpdateCheckResult updateInfo,
        CancellationToken cancellationToken = default)
    {
        var result = await _serviceUpdateService.ApplyAsync(updateInfo, cancellationToken);
        await RefreshDiscoveryAsync(cancellationToken);
        return result;
    }

    public async Task<string> BuildSupportBundleAsync(int maxDiagnostics = 200, CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        var redacted = await GetEffectiveRedactedConfigAsync(cancellationToken)
            ?? CollectorConfigRedactor.CreateRedacted(_workingConfig);
        var diagnostics = await GetDiagnosticsAsync(maxDiagnostics, cancellationToken);
        var service = await ServiceControl.QueryAsync();

        var bundle = new
        {
            generatedAtUtc = DateTime.UtcNow,
            control = new
            {
                selectedTarget = SelectedTarget?.ToString() ?? "None",
                serviceState = service.State.ToString(),
                serviceInstalled = service.Installed,
                startupType = service.StartupType
            },
            status,
            redactedConfig = redacted,
            diagnostics
        };

        return JsonSerializer.Serialize(bundle, JsonOptions);
    }

    public string ResolveCollectorExecutablePath()
    {
        // User-configured override takes priority
        if (!string.IsNullOrWhiteSpace(_collectorExeOverride) && File.Exists(_collectorExeOverride))
        {
            return _collectorExeOverride;
        }

        const string exeName = "com.logdb.windows.collector.exe";
        var baseDir = AppContext.BaseDirectory;

        var candidates = new List<string>
        {
            // Same directory as UI (bundled deployment)
            Path.Combine(baseDir, exeName),
            // Sibling "collector" folder (side-by-side layout)
            Path.GetFullPath(Path.Combine(baseDir, "..", "collector", exeName)),
            // Parent directory
            Path.GetFullPath(Path.Combine(baseDir, "..", exeName)),
            // Dev: sibling project debug output
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "com.logdb.windows.collector", "bin", "Debug", "net10.0-windows", exeName)),
            // Program Files standard install
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LogDB", "collector", exeName)
        };

        // Also check the installed service binary path (cached from last discovery)
        if (Discovery?.Service is { Installed: true, BinaryPath: { } binaryPath }
            && !string.IsNullOrWhiteSpace(binaryPath) && File.Exists(binaryPath))
        {
            candidates.Insert(0, binaryPath);
        }

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    public async Task SaveUiSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = new UiSettingsDto { CollectorExecutablePath = _collectorExeOverride };
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var dir = Path.GetDirectoryName(CollectorPathDefaults.UiSettingsPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(CollectorPathDefaults.UiSettingsPath, json, cancellationToken);
    }

    private async Task LoadUiSettingsAsync(CancellationToken cancellationToken)
    {
        var path = CollectorPathDefaults.UiSettingsPath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var settings = JsonSerializer.Deserialize<UiSettingsDto>(json, JsonOptions);
            _collectorExeOverride = settings?.CollectorExecutablePath ?? string.Empty;
        }
        catch
        {
            // best effort
        }
    }

    private sealed class UiSettingsDto
    {
        public string CollectorExecutablePath { get; set; } = string.Empty;
    }

    private async Task<ValidationResultDto> SendForValidationAsync(
        string command,
        string? payloadJson,
        CancellationToken cancellationToken)
    {
        if (SelectedTarget == null)
        {
            return new ValidationResultDto
            {
                Success = false,
                Code = "NO_TARGET",
                Message = "No local collector target selected."
            };
        }

        var response = await _controlClient.SendAsync(SelectedTarget.Value, command, payloadJson, cancellationToken: cancellationToken);
        if (!response.Success)
        {
            return new ValidationResultDto
            {
                Success = false,
                Code = "CONTROL_ERROR",
                Message = response.Message ?? "Control request failed."
            };
        }

        if (string.IsNullOrWhiteSpace(response.PayloadJson))
        {
            return new ValidationResultDto
            {
                Success = false,
                Code = "EMPTY_RESPONSE",
                Message = "Collector returned an empty validation payload."
            };
        }

        return JsonSerializer.Deserialize<ValidationResultDto>(response.PayloadJson, JsonOptions)
               ?? new ValidationResultDto
               {
                   Success = false,
                   Code = "PARSE_ERROR",
                   Message = "Failed to parse validation payload."
               };
    }

    private async Task<PreviewResultDto<T>> SendForPreviewAsync<T>(
        string command,
        int max,
        CancellationToken cancellationToken)
    {
        if (SelectedTarget == null)
        {
            return new PreviewResultDto<T>
            {
                Success = false,
                Code = "NO_TARGET",
                Message = "No local collector target selected."
            };
        }

        var request = new PreviewRequestDto { Max = max };
        var response = await _controlClient.SendAsync(
            SelectedTarget.Value,
            command,
            JsonSerializer.Serialize(request, JsonOptions),
            cancellationToken: cancellationToken);

        if (!response.Success)
        {
            return new PreviewResultDto<T>
            {
                Success = false,
                Code = "CONTROL_ERROR",
                Message = response.Message ?? "Control request failed."
            };
        }

        if (string.IsNullOrWhiteSpace(response.PayloadJson))
        {
            return new PreviewResultDto<T>
            {
                Success = false,
                Code = "EMPTY_RESPONSE",
                Message = "Collector returned an empty preview payload."
            };
        }

        return JsonSerializer.Deserialize<PreviewResultDto<T>>(response.PayloadJson, JsonOptions)
               ?? new PreviewResultDto<T>
               {
                   Success = false,
                   Code = "PARSE_ERROR",
                   Message = "Failed to parse preview payload."
               };
    }

    private static CollectorConfigDto CloneConfig(CollectorConfigDto source)
    {
        var json = JsonSerializer.Serialize(source, JsonOptions);
        return JsonSerializer.Deserialize<CollectorConfigDto>(json, JsonOptions) ?? new CollectorConfigDto();
    }
}
