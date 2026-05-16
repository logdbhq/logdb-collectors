using System.Collections.ObjectModel;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.ui.Services;
using com.logdb.windows.collector.ui.ViewModels.Infrastructure;

namespace com.logdb.windows.collector.ui.ViewModels.Pages;

public sealed class ModuleCardViewModel : ObservableObject
{
    private string _name = string.Empty;
    private bool _enabled;
    private string _state = "Stopped";
    private string _lastPoll = "-";
    private string _lastError = "-";
    private long _sentCount;
    private long _failedCount;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }

    public string LastPoll
    {
        get => _lastPoll;
        set => SetProperty(ref _lastPoll, value);
    }

    public string LastError
    {
        get => _lastError;
        set => SetProperty(ref _lastError, value);
    }

    public long SentCount
    {
        get => _sentCount;
        set => SetProperty(ref _sentCount, value);
    }

    public long FailedCount
    {
        get => _failedCount;
        set => SetProperty(ref _failedCount, value);
    }
}

public sealed class OverviewPageViewModel : PageViewModelBase
{
    private readonly LocalCollectorAdminClient _adminClient;
    private readonly Func<Task> _openDiagnosticsAction;
    private readonly Action<string, bool> _statusCallback;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string _collectorMode = "Not running";
    private string _connectionStatus = "Offline";
    private string _lastSuccessfulSend = "-";
    private long _totalSent;
    private long _totalFailed;
    private string _engineVersion = "Collector 1.0";
    private string _engineSubtitle = "Local Windows collector";
    private string _containersSummary = "0 / 0";
    private string _containersDetails = "Running: 0 | Stopped: 0";
    private string _resourcesSummary = "0 modules | 0 errors";
    private string _storageSummary = "Total: 0 MB";
    private string _storageReclaimable = "Reclaimable: -";
    private double _storageUsagePercent;
    private string _storageUsageText = "0%";
    private string _liveCpu = "-";
    private string _liveMemory = "-";
    private string _liveNetwork = "-";
    private string _activeModules = "0";
    private string _updatedAgo = "Updated now";

    public OverviewPageViewModel(
        LocalCollectorAdminClient adminClient,
        Func<Task> openDiagnosticsAction,
        Action<string, bool> statusCallback)
        : base("Overview")
    {
        _adminClient = adminClient;
        _openDiagnosticsAction = openDiagnosticsAction;
        _statusCallback = statusCallback;

        Modules = new ObservableCollection<ModuleCardViewModel>();
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync);
        ReloadConfigCommand = new AsyncRelayCommand(ReloadConfigAsync);
        OpenDiagnosticsCommand = new AsyncRelayCommand(_openDiagnosticsAction);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
    }

    public ObservableCollection<ModuleCardViewModel> Modules { get; }

    public string CollectorMode
    {
        get => _collectorMode;
        set => SetProperty(ref _collectorMode, value);
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        set => SetProperty(ref _connectionStatus, value);
    }

    public string LastSuccessfulSend
    {
        get => _lastSuccessfulSend;
        set => SetProperty(ref _lastSuccessfulSend, value);
    }

    public long TotalSent
    {
        get => _totalSent;
        set => SetProperty(ref _totalSent, value);
    }

    public long TotalFailed
    {
        get => _totalFailed;
        set => SetProperty(ref _totalFailed, value);
    }

    public string EngineVersion
    {
        get => _engineVersion;
        set => SetProperty(ref _engineVersion, value);
    }

    public string EngineSubtitle
    {
        get => _engineSubtitle;
        set => SetProperty(ref _engineSubtitle, value);
    }

    public string ContainersSummary
    {
        get => _containersSummary;
        set => SetProperty(ref _containersSummary, value);
    }

    public string ContainersDetails
    {
        get => _containersDetails;
        set => SetProperty(ref _containersDetails, value);
    }

    public string ResourcesSummary
    {
        get => _resourcesSummary;
        set => SetProperty(ref _resourcesSummary, value);
    }

    public string StorageSummary
    {
        get => _storageSummary;
        set => SetProperty(ref _storageSummary, value);
    }

    public string StorageReclaimable
    {
        get => _storageReclaimable;
        set => SetProperty(ref _storageReclaimable, value);
    }

    public double StorageUsagePercent
    {
        get => _storageUsagePercent;
        set => SetProperty(ref _storageUsagePercent, value);
    }

    public string StorageUsageText
    {
        get => _storageUsageText;
        set => SetProperty(ref _storageUsageText, value);
    }

    public string LiveCpu
    {
        get => _liveCpu;
        set => SetProperty(ref _liveCpu, value);
    }

    public string LiveMemory
    {
        get => _liveMemory;
        set => SetProperty(ref _liveMemory, value);
    }

    public string LiveNetwork
    {
        get => _liveNetwork;
        set => SetProperty(ref _liveNetwork, value);
    }

    public string ActiveModules
    {
        get => _activeModules;
        set => SetProperty(ref _activeModules, value);
    }

    public string UpdatedAgo
    {
        get => _updatedAgo;
        set => SetProperty(ref _updatedAgo, value);
    }

    public AsyncRelayCommand TestConnectionCommand { get; }
    public AsyncRelayCommand ReloadConfigCommand { get; }
    public AsyncRelayCommand OpenDiagnosticsCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public override async Task RefreshAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            var status = await _adminClient.GetStatusAsync();

            if (status == null)
            {
                Modules.Clear();
                CollectorMode = "Not running";
                ConnectionStatus = "Offline";
                LastSuccessfulSend = "-";
                TotalSent = 0;
                TotalFailed = 0;
                EngineVersion = "Collector 1.0";
                EngineSubtitle = "Local Windows collector";
                ContainersSummary = "0 / 0";
                ContainersDetails = "Running: 0 | Stopped: 0";
                ResourcesSummary = "0 modules | 0 errors";
                StorageSummary = "Total: 0 MB";
                StorageReclaimable = "Reclaimable: -";
                StorageUsagePercent = 0;
                StorageUsageText = "0%";
                LiveCpu = "-";
                LiveMemory = "-";
                LiveNetwork = "-";
                ActiveModules = "0";
                UpdatedAgo = "Updated just now";
                return;
            }

            var normalizedModules = status.Modules
                .GroupBy(module => module.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.UpdatedAtUtc).First())
                .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            CollectorMode = status.InstanceMode.ToString();
            EngineVersion = $"Collector PID {status.ProcessId}";
            EngineSubtitle = status.ConfigPath;

            var connection = await _adminClient.ValidateDestinationConnectionAsync();
            ConnectionStatus = connection.Success ? "Connected" : "Degraded";

            var lastSuccess = normalizedModules
                .Where(module => module.LastSuccessTimeUtc.HasValue)
                .Select(module => module.LastSuccessTimeUtc!.Value)
                .DefaultIfEmpty()
                .Max();

            LastSuccessfulSend = lastSuccess == default
                ? "-"
                : lastSuccess.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

            TotalSent = normalizedModules.Sum(module => module.SentCount);
            TotalFailed = normalizedModules.Sum(module => module.FailedCount);

            var running = normalizedModules.Count(module => module.State.Equals("Running", StringComparison.OrdinalIgnoreCase));
            var total = normalizedModules.Count;
            var stopped = Math.Max(0, total - running);
            ContainersSummary = $"{running} / {total}";
            ContainersDetails = $"Running: {running} | Stopped: {stopped}";
            ResourcesSummary = $"{total} modules | {TotalFailed} errors";
            ActiveModules = running.ToString();

            var diagnostics = await _adminClient.GetDiagnosticsAsync(50);
            var approxTotalMb = Math.Max(1d, diagnostics.Sum(entry => entry.Message?.Length ?? 0) / 1024d / 1024d);
            var approxUsedMb = Math.Min(approxTotalMb, approxTotalMb * (Math.Min(95d, 10d + total * 8d) / 100d));
            var usagePercent = approxTotalMb == 0 ? 0 : (approxUsedMb / approxTotalMb) * 100d;
            StorageSummary = $"Total: {approxTotalMb:F2} MB";
            StorageReclaimable = $"Reclaimable: {(approxTotalMb - approxUsedMb):F2} MB";
            StorageUsagePercent = usagePercent;
            StorageUsageText = $"{usagePercent:F1}% used";

            var metrics = await _adminClient.PreviewMetricsAsync();
            if (metrics.Success)
            {
                var cpuMetric = metrics.Rows.FirstOrDefault(row =>
                    row.Metric.Contains("cpu", StringComparison.OrdinalIgnoreCase));
                LiveCpu = cpuMetric != null ? $"{cpuMetric.Value:F0}{(string.IsNullOrWhiteSpace(cpuMetric.Unit) ? string.Empty : $" {cpuMetric.Unit}")}" : "-";

                var memoryMetric = metrics.Rows.FirstOrDefault(row =>
                    row.Metric.Contains("memory", StringComparison.OrdinalIgnoreCase));
                LiveMemory = memoryMetric != null ? $"{memoryMetric.Value:F2}{(string.IsNullOrWhiteSpace(memoryMetric.Unit) ? string.Empty : $" {memoryMetric.Unit}")}" : "-";

                var networkCount = metrics.Rows.Count(row => row.Metric.Contains("network", StringComparison.OrdinalIgnoreCase));
                LiveNetwork = networkCount > 0 ? $"{networkCount} metrics" : "-";
            }
            else
            {
                LiveCpu = "-";
                LiveMemory = "-";
                LiveNetwork = "-";
            }

            UpdatedAgo = "Updated just now";

            Modules.Clear();
            foreach (var module in normalizedModules)
            {
                Modules.Add(new ModuleCardViewModel
                {
                    Name = module.Name,
                    Enabled = module.Enabled,
                    State = module.State,
                    LastPoll = module.LastSuccessTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-",
                    LastError = string.IsNullOrWhiteSpace(module.LastError) ? "-" : module.LastError,
                    SentCount = module.SentCount,
                    FailedCount = module.FailedCount
                });
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task TestConnectionAsync()
    {
        var result = await _adminClient.TestConnectionAsync();
        _statusCallback(result.Message, result.Success);
        await RefreshAsync();
    }

    private async Task ReloadConfigAsync()
    {
        var result = await _adminClient.ReloadTargetConfigAsync();
        _statusCallback(result.Message, result.Success);
        await RefreshAsync();
    }
}
