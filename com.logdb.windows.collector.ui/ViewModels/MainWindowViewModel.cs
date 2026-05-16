using System.Collections.ObjectModel;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.ui.Services;
using com.logdb.windows.collector.ui.ViewModels.Infrastructure;
using com.logdb.windows.collector.ui.ViewModels.Pages;

namespace com.logdb.windows.collector.ui.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly LocalCollectorAdminClient _adminClient;
    private readonly VelopackUpdateService _updateService;
    private readonly Func<string, string, Task<bool>> _exportTextAsync;
    private readonly Func<string, Task> _copyToClipboardAsync;
    private readonly Action<bool> _applyThemeAction;

    public string AppVersion { get; } = $"v{typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";

    private NavigationItemViewModel? _selectedNavigationItem;
    private PageViewModelBase? _currentPage;
    private TargetOptionViewModel? _selectedTarget;
    private string _instanceSummary = "Local-only control channel (Named Pipes)";
    private string _statusMessage = "Ready";
    private bool _statusIsSuccess = true;
    private string _statusColor = "#9ED29E";
    private bool _isDarkTheme;
    private bool _isControlCenterOpen;
    private string _controlCenterModeText = "Not running";
    private string _controlCenterConnectionText = "Offline";
    private string _controlCenterServiceStateText = "NotInstalled";
    private string _controlCenterLastSuccessText = "-";
    private string _controlCenterCountersText = "Sent: 0 | Failed: 0";
    private string _controlCenterHealthText = "Offline";
    private string _controlCenterHealthColor = "#F1A18F";
    private string _controlCenterBadgeColor = "#F1A18F";
    private bool _controlCenterBadgeVisible;
    private string _controlCenterAttentionText = "No running local collector instance.";
    private string _controlCenterFirewallSummary = "Firewall: Disabled";
    private string _controlCenterFirewallRuntimeText = "Runtime: not available.";
    private string _controlCenterPrivilegeText = "Standard user";
    private bool _showApiKeyModal;
    private string _modalApiKey = string.Empty;
    private string _modalApiKeyError = string.Empty;
    private bool _toastVisible;
    private string _toastMessage = string.Empty;
    private string _toastColor = "#9ED29E";
    private CancellationTokenSource? _toastCts;

    public MainWindowViewModel(
        Func<string, string, Task<bool>> exportTextAsync,
        Func<string, Task> copyToClipboardAsync,
        Action<bool> applyThemeAction,
        bool initialDarkTheme)
    {
        _exportTextAsync = exportTextAsync;
        _copyToClipboardAsync = copyToClipboardAsync;
        _applyThemeAction = applyThemeAction;
        _isDarkTheme = initialDarkTheme;

        _adminClient = new LocalCollectorAdminClient();
        _updateService = new VelopackUpdateService();

        OverviewPage = new OverviewPageViewModel(_adminClient, OpenDiagnosticsAsync, SetStatus);
        DataSourcesPage = new DataSourcesPageViewModel(_adminClient, SetStatus);
        DestinationPage = new DestinationPageViewModel(_adminClient, SetStatus);
        DiagnosticsPage = new DiagnosticsPageViewModel(_adminClient, SetStatus, _exportTextAsync, _copyToClipboardAsync);
        ServiceManagementPage = new ServiceManagementPageViewModel(_adminClient, SetStatus);
        AdvancedPage = new AdvancedPageViewModel(_adminClient, SetStatus);

        const string iconRoot = "avares://com.logdb.windows.collector.ui/Assets/Icons/";
        var dashboardNav = new NavigationItemViewModel("Overview", "\uE80F", OverviewPage, $"{iconRoot}four-squares-icon.svg");
        var dataSourcesNav = new NavigationItemViewModel("Data Sources", "\uE7F8", DataSourcesPage, $"{iconRoot}database-line-icon.svg");
        var destinationNav = new NavigationItemViewModel("Destination", "\uE715", DestinationPage, $"{iconRoot}link-hyperlink-icon.svg");
        var diagnosticsNav = new NavigationItemViewModel("Online Console", "\uE9D9", DiagnosticsPage, $"{iconRoot}code-icon.svg");
        var serviceNav = new NavigationItemViewModel("Service Management", "\uE7C1", ServiceManagementPage, $"{iconRoot}setting-icon.svg");
        var advancedNav = new NavigationItemViewModel("Advanced", "\uE713", AdvancedPage, $"{iconRoot}warning-triangle-icon.svg");

        NavigationItems = new ObservableCollection<NavigationItemViewModel>
        {
            dashboardNav,
            dataSourcesNav,
            destinationNav,
            diagnosticsNav,
            serviceNav,
            advancedNav
        };

        PrimaryNavigationItems = new ObservableCollection<NavigationItemViewModel>
        {
            dashboardNav,
            dataSourcesNav,
            destinationNav,
            diagnosticsNav
        };

        SystemNavigationItems = new ObservableCollection<NavigationItemViewModel>
        {
            serviceNav,
            advancedNav
        };

        TargetOptions = new ObservableCollection<TargetOptionViewModel>();

        RefreshDiscoveryCommand = new AsyncRelayCommand(RefreshDiscoveryAsync);
        RefreshCurrentPageCommand = new AsyncRelayCommand(RefreshCurrentPageAsync);
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        ToggleControlCenterCommand = new RelayCommand(ToggleControlCenter);
        CloseControlCenterCommand = new RelayCommand(CloseControlCenter);
        RefreshControlCenterCommand = new AsyncRelayCommand(RefreshControlCenterAsync);
        ControlCenterTestConnectionCommand = new AsyncRelayCommand(ControlCenterTestConnectionAsync);
        ControlCenterReloadConfigCommand = new AsyncRelayCommand(ControlCenterReloadConfigAsync);
        ControlCenterApplyFirewallCommand = new AsyncRelayCommand(ControlCenterApplyFirewallAsync);
        ControlCenterRemoveFirewallCommand = new AsyncRelayCommand(ControlCenterRemoveFirewallAsync);
        ControlCenterCopySupportBundleCommand = new AsyncRelayCommand(ControlCenterCopySupportBundleAsync);
        ControlCenterOpenOverviewCommand = new RelayCommand(() => OpenPage(OverviewPage));
        ControlCenterOpenDestinationCommand = new RelayCommand(() => OpenPage(DestinationPage));
        ControlCenterOpenServiceManagementCommand = new RelayCommand(() => OpenPage(ServiceManagementPage));
        ControlCenterOpenDiagnosticsCommand = new AsyncRelayCommand(OpenDiagnosticsAsync);
        SaveModalApiKeyCommand = new AsyncRelayCommand(SaveModalApiKeyAsync);
        ExitFromApiKeyModalCommand = new RelayCommand(ExitFromApiKeyModal);
    }

    public OverviewPageViewModel OverviewPage { get; }
    public DataSourcesPageViewModel DataSourcesPage { get; }
    public DestinationPageViewModel DestinationPage { get; }
    public DiagnosticsPageViewModel DiagnosticsPage { get; }
    public ServiceManagementPageViewModel ServiceManagementPage { get; }
    public AdvancedPageViewModel AdvancedPage { get; }

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }
    public ObservableCollection<NavigationItemViewModel> PrimaryNavigationItems { get; }
    public ObservableCollection<NavigationItemViewModel> SystemNavigationItems { get; }
    public ObservableCollection<TargetOptionViewModel> TargetOptions { get; }

    public NavigationItemViewModel? SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            if (!SetProperty(ref _selectedNavigationItem, value))
            {
                return;
            }

            foreach (var item in NavigationItems)
            {
                item.IsSelected = item == value;
            }

            IsControlCenterOpen = false;
            CurrentPage = value?.Page;
            _ = RefreshCurrentPageAsync();
        }
    }

    public PageViewModelBase? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public TargetOptionViewModel? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!SetProperty(ref _selectedTarget, value))
            {
                return;
            }

            _adminClient.SetSelectedTarget(value?.Mode);
            _ = RefreshCurrentPageAsync();
            _ = RefreshControlCenterSnapshotAsync();
        }
    }

    public string InstanceSummary
    {
        get => _instanceSummary;
        set => SetProperty(ref _instanceSummary, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool StatusIsSuccess
    {
        get => _statusIsSuccess;
        set => SetProperty(ref _statusIsSuccess, value);
    }

    public string StatusColor
    {
        get => _statusColor;
        set => SetProperty(ref _statusColor, value);
    }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (!SetProperty(ref _isDarkTheme, value))
            {
                return;
            }

            _applyThemeAction(value);
            NotifyPropertyChanged(nameof(ThemeModeText));
            NotifyPropertyChanged(nameof(ThemeToggleToolTip));
        }
    }

    public string ThemeModeText => IsDarkTheme ? "Dark" : "Light";
    public string ThemeToggleToolTip => IsDarkTheme ? "Switch to light theme" : "Switch to dark theme";

    public bool IsControlCenterOpen
    {
        get => _isControlCenterOpen;
        set => SetProperty(ref _isControlCenterOpen, value);
    }

    public string ControlCenterModeText
    {
        get => _controlCenterModeText;
        private set => SetProperty(ref _controlCenterModeText, value);
    }

    public string ControlCenterConnectionText
    {
        get => _controlCenterConnectionText;
        private set => SetProperty(ref _controlCenterConnectionText, value);
    }

    public string ControlCenterServiceStateText
    {
        get => _controlCenterServiceStateText;
        private set => SetProperty(ref _controlCenterServiceStateText, value);
    }

    public string ControlCenterLastSuccessText
    {
        get => _controlCenterLastSuccessText;
        private set => SetProperty(ref _controlCenterLastSuccessText, value);
    }

    public string ControlCenterCountersText
    {
        get => _controlCenterCountersText;
        private set => SetProperty(ref _controlCenterCountersText, value);
    }

    public string ControlCenterHealthText
    {
        get => _controlCenterHealthText;
        private set => SetProperty(ref _controlCenterHealthText, value);
    }

    public string ControlCenterHealthColor
    {
        get => _controlCenterHealthColor;
        private set => SetProperty(ref _controlCenterHealthColor, value);
    }

    public string ControlCenterBadgeColor
    {
        get => _controlCenterBadgeColor;
        private set => SetProperty(ref _controlCenterBadgeColor, value);
    }

    public bool ControlCenterBadgeVisible
    {
        get => _controlCenterBadgeVisible;
        private set => SetProperty(ref _controlCenterBadgeVisible, value);
    }

    public string ControlCenterAttentionText
    {
        get => _controlCenterAttentionText;
        private set => SetProperty(ref _controlCenterAttentionText, value);
    }

    public string ControlCenterFirewallSummary
    {
        get => _controlCenterFirewallSummary;
        private set => SetProperty(ref _controlCenterFirewallSummary, value);
    }

    public string ControlCenterFirewallRuntimeText
    {
        get => _controlCenterFirewallRuntimeText;
        private set => SetProperty(ref _controlCenterFirewallRuntimeText, value);
    }

    public string ControlCenterPrivilegeText
    {
        get => _controlCenterPrivilegeText;
        private set => SetProperty(ref _controlCenterPrivilegeText, value);
    }

    public bool ShowApiKeyModal
    {
        get => _showApiKeyModal;
        set => SetProperty(ref _showApiKeyModal, value);
    }

    public string ModalApiKey
    {
        get => _modalApiKey;
        set => SetProperty(ref _modalApiKey, value);
    }

    public string ModalApiKeyError
    {
        get => _modalApiKeyError;
        set => SetProperty(ref _modalApiKeyError, value);
    }

    public bool ToastVisible
    {
        get => _toastVisible;
        set => SetProperty(ref _toastVisible, value);
    }

    public string ToastMessage
    {
        get => _toastMessage;
        set => SetProperty(ref _toastMessage, value);
    }

    public string ToastColor
    {
        get => _toastColor;
        set => SetProperty(ref _toastColor, value);
    }

    public AsyncRelayCommand SaveModalApiKeyCommand { get; }
    public RelayCommand ExitFromApiKeyModalCommand { get; }

    public Action? RequestShutdown { get; set; }

    public AsyncRelayCommand RefreshDiscoveryCommand { get; }
    public AsyncRelayCommand RefreshCurrentPageCommand { get; }
    public AsyncRelayCommand CheckUpdatesCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public RelayCommand ToggleControlCenterCommand { get; }
    public RelayCommand CloseControlCenterCommand { get; }
    public AsyncRelayCommand RefreshControlCenterCommand { get; }
    public AsyncRelayCommand ControlCenterTestConnectionCommand { get; }
    public AsyncRelayCommand ControlCenterReloadConfigCommand { get; }
    public AsyncRelayCommand ControlCenterApplyFirewallCommand { get; }
    public AsyncRelayCommand ControlCenterRemoveFirewallCommand { get; }
    public AsyncRelayCommand ControlCenterCopySupportBundleCommand { get; }
    public RelayCommand ControlCenterOpenOverviewCommand { get; }
    public RelayCommand ControlCenterOpenDestinationCommand { get; }
    public RelayCommand ControlCenterOpenServiceManagementCommand { get; }
    public AsyncRelayCommand ControlCenterOpenDiagnosticsCommand { get; }

    public async Task InitializeAsync()
    {
        await _adminClient.InitializeAsync();

        if (!_adminClient.HasApiKey)
        {
            ShowApiKeyModal = true;
            return;
        }

        await CompleteInitializationAsync();
    }

    private async Task CompleteInitializationAsync()
    {
        await RefreshDiscoveryAsync();
        SelectedNavigationItem = NavigationItems.FirstOrDefault();
        await RefreshCurrentPageAsync();
        await RefreshControlCenterSnapshotAsync();
    }

    public async Task AutoCheckUpdatesOnStartupAsync()
    {
        var autoUpdateEnabled =
            !string.Equals(Environment.GetEnvironmentVariable("LOGDB_COLLECTOR_UI_AUTO_UPDATE"), "false", StringComparison.OrdinalIgnoreCase);

        if (!autoUpdateEnabled)
        {
            return;
        }

        var result = await _updateService.CheckAndApplyAsync(silentIfNoUpdate: true);
        if (!result.Success && !result.Message.Contains("No updates", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(result.Message, false);
        }
    }

    private async Task RefreshDiscoveryAsync()
    {
        await _adminClient.RefreshDiscoveryAsync();
        TargetOptions.Clear();

        foreach (var mode in _adminClient.GetAvailableTargets())
        {
            var label = mode switch
            {
                CollectorInstanceMode.Service => "Service instance (local)",
                CollectorInstanceMode.Console => "Console instance (local)",
                _ => mode.ToString()
            };
            TargetOptions.Add(new TargetOptionViewModel(mode, label));
        }

        SelectedTarget = TargetOptions.FirstOrDefault(option => option.Mode == _adminClient.SelectedTarget);

        var parts = new List<string>
        {
            "Local-only control channel: Named Pipes"
        };
        if (_adminClient.Discovery?.ServiceEndpoint.IsReachable == true)
        {
            parts.Add("Service instance detected (recommended for servers).");
        }
        if (_adminClient.Discovery?.ConsoleEndpoint.IsReachable == true)
        {
            parts.Add("Console instance detected (recommended for testing).");
        }
        if (!_adminClient.GetAvailableTargets().Any())
        {
            parts.Add("No running local collector instance.");
        }

        InstanceSummary = string.Join(" ", parts);

        await ServiceManagementPage.RefreshAsync();
        await RefreshControlCenterSnapshotAsync();
    }

    private async Task RefreshCurrentPageAsync()
    {
        if (CurrentPage != null)
        {
            await CurrentPage.RefreshAsync();
        }
    }

    private async Task CheckUpdatesAsync()
    {
        var result = await _updateService.CheckAndApplyAsync(silentIfNoUpdate: false);
        SetStatus(result.Message, result.Success);
    }

    private async Task OpenDiagnosticsAsync()
    {
        var diagnosticsItem = NavigationItems.FirstOrDefault(item => item.Page == DiagnosticsPage);
        if (diagnosticsItem != null)
        {
            SelectedNavigationItem = diagnosticsItem;
            await DiagnosticsPage.RefreshAsync();
            IsControlCenterOpen = false;
        }
    }

    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
    }

    private void ToggleControlCenter()
    {
        IsControlCenterOpen = !IsControlCenterOpen;
        if (IsControlCenterOpen)
        {
            _ = RefreshControlCenterSnapshotAsync();
        }
    }

    private void CloseControlCenter()
    {
        IsControlCenterOpen = false;
    }

    private async Task RefreshControlCenterAsync()
    {
        await RefreshDiscoveryAsync();
        await RefreshCurrentPageAsync();
        await RefreshControlCenterSnapshotAsync();
    }

    private async Task ControlCenterTestConnectionAsync()
    {
        var result = await _adminClient.TestConnectionAsync();
        SetStatus(result.Message, result.Success);
        await RefreshControlCenterSnapshotAsync();
    }

    private async Task ControlCenterReloadConfigAsync()
    {
        var result = await _adminClient.ReloadTargetConfigAsync();
        SetStatus(result.Message, result.Success);
        await RefreshControlCenterSnapshotAsync();
    }

    private async Task ControlCenterApplyFirewallAsync()
    {
        if (!ServiceControl.IsAdministrator())
        {
            SetStatus("Run as Administrator to apply firewall rules.", false);
            return;
        }

        var result = await _adminClient.ApplyFirewallAsync();
        SetStatus(result.Message, result.Success);
        await RefreshControlCenterSnapshotAsync();
    }

    private async Task ControlCenterRemoveFirewallAsync()
    {
        if (!ServiceControl.IsAdministrator())
        {
            SetStatus("Run as Administrator to remove firewall rules.", false);
            return;
        }

        var result = await _adminClient.RemoveFirewallAsync();
        SetStatus(result.Message, result.Success);
        await RefreshControlCenterSnapshotAsync();
    }

    private async Task ControlCenterCopySupportBundleAsync()
    {
        var bundle = await _adminClient.BuildSupportBundleAsync();
        await _copyToClipboardAsync(bundle);
        SetStatus("Support bundle copied to clipboard.", true);
    }

    private void OpenPage(PageViewModelBase page)
    {
        var item = NavigationItems.FirstOrDefault(nav => nav.Page == page);
        if (item != null)
        {
            SelectedNavigationItem = item;
            IsControlCenterOpen = false;
        }
    }

    private async Task RefreshControlCenterSnapshotAsync()
    {
        ControlCenterPrivilegeText = ServiceControl.IsAdministrator() ? "Administrator" : "Standard user";

        var selectedMode = _adminClient.SelectedTarget;
        ControlCenterModeText = selectedMode?.ToString() ?? "Not running";
        ControlCenterServiceStateText = _adminClient.Discovery?.Service.State.ToString() ?? "Unknown";

        var serviceReachable = _adminClient.Discovery?.ServiceEndpoint.IsReachable == true;
        var consoleReachable = _adminClient.Discovery?.ConsoleEndpoint.IsReachable == true;
        var anyReachable = serviceReachable || consoleReachable;
        ControlCenterConnectionText = anyReachable ? "Connected (local)" : "Offline";
        ControlCenterBadgeVisible = anyReachable;
        var status = anyReachable ? await _adminClient.GetStatusAsync() : null;

        if (!anyReachable)
        {
            ControlCenterLastSuccessText = "-";
            ControlCenterCountersText = "Sent: 0 | Failed: 0";
            ControlCenterHealthText = "Offline";
            ControlCenterHealthColor = "#F1A18F";
            ControlCenterBadgeColor = ControlCenterHealthColor;
            ControlCenterAttentionText = "No running local collector instance. Start service or console mode.";
        }
        else
        {
            var modules = status?.Modules ?? new List<ModuleStatusDto>();
            var lastSuccess = modules
                .Where(module => module.LastSuccessTimeUtc.HasValue)
                .Select(module => module.LastSuccessTimeUtc!.Value)
                .DefaultIfEmpty()
                .Max();

            ControlCenterLastSuccessText = lastSuccess == default
                ? "-"
                : lastSuccess.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

            var sentCount = modules.Sum(module => module.SentCount);
            var failedCount = modules.Sum(module => module.FailedCount);
            ControlCenterCountersText = $"Sent: {sentCount} | Failed: {failedCount}";

            var hasErrors = modules.Any(module =>
                !string.IsNullOrWhiteSpace(module.LastError) ||
                module.State.Contains("Error", StringComparison.OrdinalIgnoreCase));
            var runningEnabled = modules.Any(module =>
                module.Enabled &&
                (module.State.Equals("Running", StringComparison.OrdinalIgnoreCase) ||
                 module.State.Equals("Applied", StringComparison.OrdinalIgnoreCase)));

            if (hasErrors)
            {
                ControlCenterHealthText = "Degraded";
                ControlCenterHealthColor = "#F1A18F";
            }
            else if (runningEnabled)
            {
                ControlCenterHealthText = "Healthy";
                ControlCenterHealthColor = "#9ED29E";
            }
            else
            {
                ControlCenterHealthText = "Idle";
                ControlCenterHealthColor = "#E4C187";
            }

            ControlCenterBadgeColor = ControlCenterHealthColor;

            var missingApiKey = !_adminClient.HasApiKey;
            ControlCenterAttentionText = missingApiKey
                ? "API key is missing. Open Destination and save API key."
                : hasErrors
                    ? "One or more modules reported errors. Open Diagnostics."
                    : "No immediate action required.";
        }

        var firewall = _adminClient.SnapshotWorkingConfig().Firewall;
        ControlCenterFirewallSummary = firewall.Enabled
            ? $"Firewall sync enabled (every {firewall.PollIntervalSeconds}s)"
            : "Firewall sync disabled";

        var firewallStatus = status?.Modules
            .FirstOrDefault(module => module.Name.Equals("Firewall", StringComparison.OrdinalIgnoreCase));
        if (firewallStatus == null)
        {
            ControlCenterFirewallRuntimeText = "Runtime: unavailable.";
        }
        else
        {
            ControlCenterFirewallRuntimeText = string.IsNullOrWhiteSpace(firewallStatus.LastError)
                ? $"Runtime: {firewallStatus.State}"
                : $"Runtime: {firewallStatus.State} ({firewallStatus.LastError})";
        }
    }

    private async Task SaveModalApiKeyAsync()
    {
        var key = ModalApiKey.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            ModalApiKeyError = "API key cannot be empty.";
            return;
        }

        ModalApiKeyError = string.Empty;

        if (_adminClient.SelectedTarget != null)
        {
            var config = _adminClient.SnapshotWorkingConfig();
            var result = await _adminClient.ApplyConfigAsync(config, replacementApiKey: key);
            if (!result.Success)
            {
                ModalApiKeyError = result.Message;
                return;
            }
        }
        else
        {
            await _adminClient.SaveApiKeyToDiskAsync(key);
        }

        ShowApiKeyModal = false;
        ModalApiKey = string.Empty;
        await CompleteInitializationAsync();
    }

    private void ExitFromApiKeyModal()
    {
        RequestShutdown?.Invoke();
    }

    private void SetStatus(string message, bool success)
    {
        StatusMessage = message;
        StatusIsSuccess = success;
        StatusColor = success ? "#9ED29E" : "#F1A18F";
        ShowToast(message, success);
    }

    private void ShowToast(string message, bool success)
    {
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;

        ToastMessage = message;
        ToastColor = success ? "#35B37E" : "#DE7474";
        ToastVisible = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000, token);
                if (!token.IsCancellationRequested)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ToastVisible = false);
                }
            }
            catch (OperationCanceledException)
            {
                // replaced by newer toast
            }
        }, token);
    }
}
