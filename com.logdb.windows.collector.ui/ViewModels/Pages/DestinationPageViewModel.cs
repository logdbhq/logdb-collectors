using com.logdb.windows.collector.ui.Services;
using com.logdb.windows.collector.ui.ViewModels.Infrastructure;

namespace com.logdb.windows.collector.ui.ViewModels.Pages;

public sealed class DestinationPageViewModel : PageViewModelBase
{
    private readonly LocalCollectorAdminClient _adminClient;
    private readonly Action<string, bool> _statusCallback;

    private bool _apiKeySaved;
    private bool _isReplacingApiKey;
    private string _newApiKey = string.Empty;
    private string _endpoint = string.Empty;
    private string _discoveryUrl = string.Empty;
    private string _protocol = "Native";
    private string _defaultApplication = "LogDB Collector";
    private string _defaultEnvironment = "Production";
    private string _defaultCollection = "windows";
    private string _validationMessage = "-";
    private string _customerName = string.Empty;
    private string _resolvedEndpointDisplay = "Not resolved yet.";

    public DestinationPageViewModel(LocalCollectorAdminClient adminClient, Action<string, bool> statusCallback)
        : base("Destination")
    {
        _adminClient = adminClient;
        _statusCallback = statusCallback;

        SaveCommand = new AsyncRelayCommand(SaveAsync);
        SaveApiKeyCommand = new AsyncRelayCommand(SaveApiKeyAsync);
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync);
        ReplaceApiKeyCommand = new RelayCommand(StartReplacingApiKey);
        CancelReplaceApiKeyCommand = new RelayCommand(CancelReplaceApiKey);
    }

    public bool ApiKeySaved
    {
        get => _apiKeySaved;
        set
        {
            if (!SetProperty(ref _apiKeySaved, value))
            {
                return;
            }

            NotifyPropertyChanged(nameof(ApiKeyStatusText));
            NotifyPropertyChanged(nameof(CanSaveDestinationSettings));
        }
    }

    public bool IsReplacingApiKey
    {
        get => _isReplacingApiKey;
        set
        {
            if (!SetProperty(ref _isReplacingApiKey, value))
            {
                return;
            }

            if (!value)
            {
                NewApiKey = string.Empty;
            }

            NotifyPropertyChanged(nameof(ApiKeyStatusText));
            NotifyPropertyChanged(nameof(ShowReplaceApiKeyAction));
            NotifyPropertyChanged(nameof(CanSaveDestinationSettings));
        }
    }

    public bool ShowReplaceApiKeyAction => !IsReplacingApiKey;
    public bool CanSaveDestinationSettings => !IsReplacingApiKey && ApiKeySaved;

    public string NewApiKey
    {
        get => _newApiKey;
        set
        {
            if (!SetProperty(ref _newApiKey, value))
            {
                return;
            }
            
            NotifyPropertyChanged(nameof(ApiKeyStatusText));
        }
    }

    public string Endpoint
    {
        get => _endpoint;
        set => SetProperty(ref _endpoint, value);
    }

    public string DiscoveryUrl
    {
        get => _discoveryUrl;
        set => SetProperty(ref _discoveryUrl, value);
    }

    public string Protocol
    {
        get => _protocol;
        set => SetProperty(ref _protocol, value);
    }

    public IReadOnlyList<string> ProtocolOptions { get; } = new[] { "Native", "OpenTelemetry", "Rest" };

    public string DefaultApplication
    {
        get => _defaultApplication;
        set => SetProperty(ref _defaultApplication, value);
    }

    public string DefaultEnvironment
    {
        get => _defaultEnvironment;
        set => SetProperty(ref _defaultEnvironment, value);
    }

    public string DefaultCollection
    {
        get => _defaultCollection;
        set => SetProperty(ref _defaultCollection, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        set => SetProperty(ref _validationMessage, value);
    }

    public string ApiKeyStatusText
    {
        get
        {
            if (IsReplacingApiKey)
            {
                return string.IsNullOrWhiteSpace(NewApiKey)
                    ? "Enter new key, then click Save Key."
                    : "New key pending save.";
            }

            return ApiKeySaved ? "API key saved." : "API key required.";
        }
    }

    public string CustomerName
    {
        get => _customerName;
        private set
        {
            if (SetProperty(ref _customerName, value))
            {
                NotifyPropertyChanged(nameof(HasCustomerName));
            }
        }
    }

    public bool HasCustomerName => !string.IsNullOrWhiteSpace(_customerName);

    /// <summary>
    /// Read-only display of where logs are actually being sent.
    /// Resolved from discovery on page entry / after key replace.
    /// </summary>
    public string ResolvedEndpointDisplay
    {
        get => _resolvedEndpointDisplay;
        private set => SetProperty(ref _resolvedEndpointDisplay, value);
    }

    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand SaveApiKeyCommand { get; }
    public AsyncRelayCommand TestConnectionCommand { get; }
    public RelayCommand ReplaceApiKeyCommand { get; }
    public RelayCommand CancelReplaceApiKeyCommand { get; }

    public override async Task RefreshAsync()
    {
        var config = _adminClient.SnapshotWorkingConfig();

        Endpoint = config.LogDB.Endpoint ?? string.Empty;
        DiscoveryUrl = config.LogDB.DiscoveryUrl ?? string.Empty;
        Protocol = string.IsNullOrWhiteSpace(config.LogDB.Protocol) ? "Native" : config.LogDB.Protocol;
        DefaultApplication = string.IsNullOrWhiteSpace(config.LogDB.DefaultApplication)
            ? "LogDB Collector"
            : config.LogDB.DefaultApplication;
        DefaultEnvironment = string.IsNullOrWhiteSpace(config.LogDB.DefaultEnvironment)
            ? "Production"
            : config.LogDB.DefaultEnvironment;
        DefaultCollection = string.IsNullOrWhiteSpace(config.LogDB.DefaultCollection)
            ? "windows"
            : config.LogDB.DefaultCollection;

        ApiKeySaved = _adminClient.HasApiKey;
        if (!IsReplacingApiKey)
        {
            NewApiKey = string.Empty;
        }
        NotifyPropertyChanged(nameof(ApiKeyStatusText));

        await RefreshCustomerNameAsync();
        await RefreshResolvedEndpointAsync();
    }

    private async Task RefreshCustomerNameAsync()
    {
        if (!_adminClient.HasApiKey)
        {
            CustomerName = string.Empty;
            return;
        }

        var (_, accountName) = await _adminClient.ResolveOwnerAsync();
        CustomerName = accountName ?? string.Empty;
    }

    private async Task RefreshResolvedEndpointAsync()
    {
        var config = _adminClient.SnapshotWorkingConfig();
        var explicitOverride = config.LogDB.Endpoint;
        if (!string.IsNullOrWhiteSpace(explicitOverride))
        {
            ResolvedEndpointDisplay = $"{explicitOverride}  (manual override — set in raw config on Advanced page)";
            return;
        }

        if (!_adminClient.HasApiKey)
        {
            ResolvedEndpointDisplay = "Set an API key — endpoint is resolved automatically from discovery.";
            return;
        }

        ResolvedEndpointDisplay = "Resolving from discovery service…";
        var result = await _adminClient.ResolveGrpcLoggerEndpointAsync();

        ResolvedEndpointDisplay = result.Source switch
        {
            LocalCollectorAdminClient.EndpointResolutionSource.Discovery =>
                result.Endpoint!,
            LocalCollectorAdminClient.EndpointResolutionSource.ServiceCache =>
                $"{result.Endpoint}  (cached by collector service{FormatCacheAge(result.ResolvedAtUtc)} — " +
                $"discovery currently {FormatDiscoveryProblem(result)})",
            _ =>
                $"Discovery unreachable — {FormatDiscoveryProblem(result)}. " +
                "Set LogDB.Endpoint manually on the Advanced page to bypass discovery.",
        };
    }

    private static string FormatDiscoveryProblem(LocalCollectorAdminClient.EndpointResolution r)
    {
        if (r.LastDiscoveryStatusCode.HasValue)
        {
            return r.LastDiscoveryStatusCode.Value switch
            {
                404 => "returned 404 (API key not recognized by discovery)",
                504 => "returned 504 (Postgres slow on the discovery server)",
                502 => "returned 502 (discovery upstream down)",
                _   => $"returned HTTP {r.LastDiscoveryStatusCode.Value}",
            };
        }
        return r.LastError ?? "unreachable";
    }

    private static string FormatCacheAge(DateTime? resolvedAtUtc)
    {
        if (!resolvedAtUtc.HasValue) return string.Empty;
        var age = DateTime.UtcNow - resolvedAtUtc.Value;
        if (age.TotalSeconds < 60) return ", just now";
        if (age.TotalMinutes < 60) return $", {(int)age.TotalMinutes} min ago";
        if (age.TotalHours < 24) return $", {(int)age.TotalHours} h ago";
        return $", {(int)age.TotalDays} d ago";
    }

    private async Task SaveAsync()
    {
        var errors = ValidateInput();
        if (!string.IsNullOrWhiteSpace(errors))
        {
            ValidationMessage = errors;
            _statusCallback(errors, false);
            return;
        }

        var config = _adminClient.SnapshotWorkingConfig();
        config.LogDB.Endpoint = string.IsNullOrWhiteSpace(Endpoint) ? null : Endpoint.Trim();
        config.LogDB.DiscoveryUrl = string.IsNullOrWhiteSpace(DiscoveryUrl) ? null : DiscoveryUrl.Trim();
        config.LogDB.Protocol = string.IsNullOrWhiteSpace(Protocol) ? "Native" : Protocol.Trim();
        config.LogDB.DefaultApplication = DefaultApplication.Trim();
        config.LogDB.DefaultEnvironment = DefaultEnvironment.Trim();
        config.LogDB.DefaultCollection = DefaultCollection.Trim();

        var apply = await _adminClient.ApplyConfigAsync(config, replacementApiKey: null);
        _statusCallback(apply.Message, apply.Success);
        ValidationMessage = apply.Message;

        if (apply.Success)
        {
            NotifyPropertyChanged(nameof(CanSaveDestinationSettings));
        }
    }

    private async Task SaveApiKeyAsync()
    {
        var key = NewApiKey.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            ValidationMessage = "Enter API key before saving.";
            _statusCallback(ValidationMessage, false);
            return;
        }

        var currentConfig = _adminClient.SnapshotWorkingConfig();
        var apply = await _adminClient.ApplyConfigAsync(currentConfig, replacementApiKey: key);
        _statusCallback(apply.Message, apply.Success);
        ValidationMessage = apply.Message;

        if (apply.Success)
        {
            ApiKeySaved = true;
            IsReplacingApiKey = false;
            NewApiKey = string.Empty;
            ValidationMessage = "API key saved.";
            await RefreshCustomerNameAsync();
            await RefreshResolvedEndpointAsync();
        }
    }

    private async Task TestConnectionAsync()
    {
        var result = await _adminClient.ValidateDestinationConnectionAsync();
        ValidationMessage = result.Message;
        _statusCallback(result.Message, result.Success);
    }

    private void CancelReplaceApiKey()
    {
        NewApiKey = string.Empty;
        IsReplacingApiKey = false;
        NotifyPropertyChanged(nameof(ApiKeyStatusText));
    }

    private void StartReplacingApiKey()
    {
        IsReplacingApiKey = true;
        NotifyPropertyChanged(nameof(ApiKeyStatusText));
    }

    private string ValidateInput()
    {
        if (IsReplacingApiKey)
        {
            return "API key replacement is in progress. Click Save Key or Cancel Replace first.";
        }

        if (string.IsNullOrWhiteSpace(Endpoint) && string.IsNullOrWhiteSpace(DiscoveryUrl))
        {
            return "Provide either Endpoint or Discovery URL.";
        }

        if (!ApiKeySaved)
        {
            return "API key is required. Click Replace Key and save it first.";
        }

        if (string.IsNullOrWhiteSpace(DefaultApplication))
        {
            return "Default Application is required.";
        }

        if (string.IsNullOrWhiteSpace(DefaultEnvironment))
        {
            return "Default Environment is required.";
        }

        if (string.IsNullOrWhiteSpace(DefaultCollection))
        {
            return "Default Collection is required.";
        }

        return string.Empty;
    }
}
