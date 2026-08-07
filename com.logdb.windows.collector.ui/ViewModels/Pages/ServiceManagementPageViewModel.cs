using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.ui.Services;
using com.logdb.windows.collector.ui.ViewModels.Infrastructure;

namespace com.logdb.windows.collector.ui.ViewModels.Pages;

public sealed class ServiceManagementPageViewModel : PageViewModelBase
{
    private readonly LocalCollectorAdminClient _adminClient;
    private readonly Action<string, bool> _statusCallback;

    private bool _serviceInstalled;
    private ServiceStateKind _serviceStateKind = ServiceStateKind.NotInstalled;
    private string _serviceState = "NotInstalled";
    private string _startupType = "Unknown";
    private string _serviceBinaryPath = "-";
    private string _serviceCurrentVersion = "Unknown";
    private string _serviceLatestVersion = "-";
    private bool _serviceUpdateAvailable;
    private string _serviceUpdateMessage = "Not checked.";
    private bool _isAdministrator;
    private string _modeHint = string.Empty;
    private bool _consoleRunning;
    private string _consoleStatus = "Not running";
    private string _consoleHint = "Console mode is for local testing.";
    private ServiceUpdateCheckResult? _lastUpdateCheck;
    private string _collectorExePath = string.Empty;

    public ServiceManagementPageViewModel(LocalCollectorAdminClient adminClient, Action<string, bool> statusCallback)
        : base("Service Management")
    {
        _adminClient = adminClient;
        _statusCallback = statusCallback;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);

        RestartElevatedCommand = new RelayCommand(RestartElevated);
        InstallServiceCommand = new AsyncRelayCommand(InstallServiceAsync);
        UninstallServiceCommand = new AsyncRelayCommand(UninstallServiceAsync);
        StartServiceCommand = new AsyncRelayCommand(StartServiceAsync);
        StopServiceCommand = new AsyncRelayCommand(StopServiceAsync);
        RestartServiceCommand = new AsyncRelayCommand(RestartServiceAsync);
        CheckServiceUpdateCommand = new AsyncRelayCommand(CheckServiceUpdateAsync);
        ApplyServiceUpdateCommand = new AsyncRelayCommand(ApplyServiceUpdateAsync);
        SaveCollectorPathCommand = new AsyncRelayCommand(SaveCollectorPathAsync);

        RunConsoleCommand = new AsyncRelayCommand(RunConsoleAsync);
        StopConsoleCommand = new AsyncRelayCommand(StopConsoleAsync);
        RestartConsoleCommand = new AsyncRelayCommand(RestartConsoleAsync);
    }

    public string ServiceName => ServiceControl.ServiceName;

    public bool ServiceInstalled
    {
        get => _serviceInstalled;
        set
        {
            if (!SetProperty(ref _serviceInstalled, value))
            {
                return;
            }

            NotifyActionStateChanged();
        }
    }

    public string ServiceState
    {
        get => _serviceState;
        set
        {
            if (!SetProperty(ref _serviceState, value))
            {
                return;
            }

            NotifyActionStateChanged();
            NotifyPropertyChanged(nameof(ServiceStateSummary));
        }
    }

    public string StartupType
    {
        get => _startupType;
        set => SetProperty(ref _startupType, value);
    }

    public string ServiceBinaryPath
    {
        get => _serviceBinaryPath;
        set => SetProperty(ref _serviceBinaryPath, value);
    }

    public string ServiceCurrentVersion
    {
        get => _serviceCurrentVersion;
        set => SetProperty(ref _serviceCurrentVersion, value);
    }

    public string ServiceLatestVersion
    {
        get => _serviceLatestVersion;
        set => SetProperty(ref _serviceLatestVersion, value);
    }

    public bool ServiceUpdateAvailable
    {
        get => _serviceUpdateAvailable;
        set
        {
            if (!SetProperty(ref _serviceUpdateAvailable, value))
            {
                return;
            }

            NotifyActionStateChanged();
        }
    }

    public string ServiceUpdateMessage
    {
        get => _serviceUpdateMessage;
        set => SetProperty(ref _serviceUpdateMessage, value);
    }

    public bool IsAdministrator
    {
        get => _isAdministrator;
        set
        {
            if (!SetProperty(ref _isAdministrator, value))
            {
                return;
            }

            NotifyActionStateChanged();
            NotifyPropertyChanged(nameof(PrivilegeModeText));
        }
    }

    public string ModeHint
    {
        get => _modeHint;
        set => SetProperty(ref _modeHint, value);
    }

    public bool ConsoleRunning
    {
        get => _consoleRunning;
        private set
        {
            if (!SetProperty(ref _consoleRunning, value))
            {
                return;
            }

            NotifyActionStateChanged();
        }
    }

    public string ConsoleStatus
    {
        get => _consoleStatus;
        private set => SetProperty(ref _consoleStatus, value);
    }

    public string ConsoleHint
    {
        get => _consoleHint;
        private set => SetProperty(ref _consoleHint, value);
    }

    public string PrivilegeModeText => IsAdministrator ? "Administrator" : "Standard user";
    public string ServiceStateSummary => ServiceInstalled
        ? (_serviceStateKind == ServiceStateKind.Running ? "Production service is running." : "Service is installed but not running.")
        : "Service is not installed.";

    /// <summary>True when the UI lacks the elevated token every sc.exe call in
    /// this card needs — drives the "Restart as Administrator" button and the
    /// explanation next to the greyed-out buttons.</summary>
    public bool NeedsElevation => !IsAdministrator;

    /// <summary>Says *why* the buttons are disabled. Empty when elevated, so the
    /// hint collapses instead of stating the obvious.</summary>
    public string ElevationHint => IsAdministrator
        ? string.Empty
        : "Disabled — the UI is running as a standard user. Restart as Administrator to install, start, stop, or restart the service.";

    public bool CanInstallService => IsAdministrator && !ServiceInstalled;
    public bool CanUninstallService => IsAdministrator && ServiceInstalled;
    public bool CanStartService => IsAdministrator && ServiceInstalled && _serviceStateKind != ServiceStateKind.Running;
    public bool CanStopService => IsAdministrator && ServiceInstalled && _serviceStateKind == ServiceStateKind.Running;
    public bool CanRestartService => IsAdministrator && ServiceInstalled && _serviceStateKind == ServiceStateKind.Running;

    public bool CanCheckServiceUpdate => ServiceInstalled;
    public bool CanApplyServiceUpdate => IsAdministrator && ServiceInstalled && ServiceUpdateAvailable;

    public bool CanRunConsole => !ConsoleRunning && _serviceStateKind != ServiceStateKind.Running;
    public bool CanStopConsole => ConsoleRunning;
    public bool CanRestartConsole => ConsoleRunning;
    public string CollectorExePath
    {
        get => _collectorExePath;
        set => SetProperty(ref _collectorExePath, value);
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand RestartElevatedCommand { get; }
    public AsyncRelayCommand InstallServiceCommand { get; }
    public AsyncRelayCommand UninstallServiceCommand { get; }
    public AsyncRelayCommand StartServiceCommand { get; }
    public AsyncRelayCommand StopServiceCommand { get; }
    public AsyncRelayCommand RestartServiceCommand { get; }
    public AsyncRelayCommand CheckServiceUpdateCommand { get; }
    public AsyncRelayCommand ApplyServiceUpdateCommand { get; }
    public AsyncRelayCommand SaveCollectorPathCommand { get; }
    public AsyncRelayCommand RunConsoleCommand { get; }
    public AsyncRelayCommand StopConsoleCommand { get; }
    public AsyncRelayCommand RestartConsoleCommand { get; }

    public override async Task RefreshAsync()
    {
        IsAdministrator = ServiceControl.IsAdministrator();

        await _adminClient.RefreshDiscoveryAsync();
        var query = _adminClient.Discovery?.Service ?? await ServiceControl.QueryAsync();
        ServiceInstalled = query.Installed;
        _serviceStateKind = query.State;
        ServiceState = query.State.ToString();
        StartupType = query.StartupType;
        ServiceBinaryPath = string.IsNullOrWhiteSpace(query.BinaryPath) ? "-" : query.BinaryPath;
        ServiceCurrentVersion = query.BinaryVersion;

        var serviceEndpointReachable = _adminClient.Discovery?.ServiceEndpoint.IsReachable == true;
        var consoleEndpointReachable = _adminClient.Discovery?.ConsoleEndpoint.IsReachable == true;
        ConsoleRunning = consoleEndpointReachable;
        ConsoleStatus = consoleEndpointReachable ? "Running" : "Not running";
        ConsoleHint = serviceEndpointReachable
            ? "Console start is disabled while service mode is running to avoid control endpoint collision."
            : "Use console mode for try-it-now / local testing without service installation.";

        ModeHint = IsAdministrator
            ? "Administrative actions are available for service lifecycle and service update."
            : "Running as a standard user: service lifecycle and service update are disabled. Use \"Restart as Administrator\" on the Windows Service card to elevate.";

        CollectorExePath = _adminClient.CollectorExeOverride;

        NotifyActionStateChanged();
        NotifyPropertyChanged(nameof(ServiceStateSummary));
        NotifyPropertyChanged(nameof(PrivilegeModeText));
    }

    /// <summary>
    /// Hands over to an elevated copy of the UI and closes this one, so the user
    /// isn't left staring at two windows. If UAC is declined the spawn fails and
    /// we stay put — the buttons simply remain disabled.
    /// </summary>
    private void RestartElevated()
    {
        if (IsAdministrator)
        {
            return;
        }

        if (!ServiceControl.RelaunchElevated())
        {
            _statusCallback("Elevation cancelled — still running as a standard user.", false);
            return;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private async Task InstallServiceAsync()
    {
        if (!EnsureAdmin("Installing service"))
        {
            return;
        }

        var result = await _adminClient.InstallServiceAsync();
        _statusCallback(result.Message, result.Success);
        await RefreshAsync();
    }

    private async Task UninstallServiceAsync()
    {
        if (!EnsureAdmin("Uninstalling service"))
        {
            return;
        }

        var result = await _adminClient.UninstallServiceAsync();
        _statusCallback(result.Message, result.Success);
        await RefreshAsync();
    }

    private async Task StartServiceAsync()
    {
        if (!EnsureAdmin("Starting service"))
        {
            return;
        }

        var result = await _adminClient.StartServiceAsync();
        _statusCallback(result.Message, result.Success);
        await RefreshAsync();
    }

    private async Task StopServiceAsync()
    {
        if (!EnsureAdmin("Stopping service"))
        {
            return;
        }

        var result = await _adminClient.StopServiceAsync();
        _statusCallback(result.Message, result.Success);
        await RefreshAsync();
    }

    private async Task RestartServiceAsync()
    {
        if (!EnsureAdmin("Restarting service"))
        {
            return;
        }

        var result = await _adminClient.RestartServiceAsync();
        _statusCallback(result.Message, result.Success);
        await RefreshAsync();
    }

    private async Task RunConsoleAsync()
    {
        var resolved = _adminClient.ResolveCollectorExecutablePath();
        if (!File.Exists(resolved))
        {
            _statusCallback($"Collector executable not found: {resolved}. Use Browse to select the correct path.", false);
            return;
        }

        var result = await _adminClient.RunConsoleInstanceAsync();
        _statusCallback(result.Message, result.Success);
        await RefreshAsync();
    }

    private async Task StopConsoleAsync()
    {
        var result = await _adminClient.StopConsoleInstanceAsync();
        _statusCallback(result.Message, result.Success);
        await RefreshAsync();
    }

    private async Task RestartConsoleAsync()
    {
        var stop = await _adminClient.StopConsoleInstanceAsync();
        if (!stop.Success)
        {
            _statusCallback(stop.Message, false);
            await RefreshAsync();
            return;
        }

        await Task.Delay(1000);
        var start = await _adminClient.RunConsoleInstanceAsync();
        _statusCallback(start.Success ? "Console restarted." : start.Message, start.Success);
        await RefreshAsync();
    }

    private async Task SaveCollectorPathAsync()
    {
        _adminClient.CollectorExeOverride = CollectorExePath;
        await _adminClient.SaveUiSettingsAsync();
        var resolved = _adminClient.ResolveCollectorExecutablePath();
        var exists = File.Exists(resolved);
        _statusCallback(exists ? $"Collector path saved. Resolved: {resolved}" : $"Path saved but file not found: {resolved}", exists);
    }

    private async Task CheckServiceUpdateAsync()
    {
        var result = await _adminClient.CheckServiceUpdateAsync();
        _lastUpdateCheck = result;

        ServiceCurrentVersion = result.CurrentVersion;
        ServiceLatestVersion = result.LatestVersion;
        ServiceUpdateAvailable = result.UpdateAvailable;
        ServiceUpdateMessage = result.Message;

        _statusCallback(result.Message, result.Success);
        NotifyActionStateChanged();
    }

    private async Task ApplyServiceUpdateAsync()
    {
        if (!EnsureAdmin("Updating service"))
        {
            return;
        }

        if (_lastUpdateCheck == null)
        {
            _statusCallback("Check for service updates first.", false);
            return;
        }

        var result = await _adminClient.ApplyServiceUpdateAsync(_lastUpdateCheck);
        _statusCallback(result.Message, result.Success);

        await RefreshAsync();
        await CheckServiceUpdateAsync();
    }

    private void NotifyActionStateChanged()
    {
        NotifyPropertyChanged(nameof(CanInstallService));
        NotifyPropertyChanged(nameof(CanUninstallService));
        NotifyPropertyChanged(nameof(CanStartService));
        NotifyPropertyChanged(nameof(CanStopService));
        NotifyPropertyChanged(nameof(CanRestartService));
        NotifyPropertyChanged(nameof(CanCheckServiceUpdate));
        NotifyPropertyChanged(nameof(CanApplyServiceUpdate));
        NotifyPropertyChanged(nameof(CanRunConsole));
        NotifyPropertyChanged(nameof(CanStopConsole));
        NotifyPropertyChanged(nameof(CanRestartConsole));
        NotifyPropertyChanged(nameof(NeedsElevation));
        NotifyPropertyChanged(nameof(ElevationHint));
    }

    private bool EnsureAdmin(string action)
    {
        if (ServiceControl.IsAdministrator())
        {
            return true;
        }

        _statusCallback($"{action} requires Administrator privileges.", false);
        return false;
    }

}
