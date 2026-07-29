using System.Collections.ObjectModel;
using Avalonia.Threading;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.ui.Services;
using com.logdb.windows.collector.ui.ViewModels.Infrastructure;

namespace com.logdb.windows.collector.ui.ViewModels.Pages;

/// <summary>One captured record in the "Recent records" grid.</summary>
public sealed class RecentRecordItemViewModel
{
    public RecentRecordItemViewModel(RecentRecordDto dto)
    {
        WhenLocal = dto.WhenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        Module = dto.Module;
        Collection = dto.Collection;
        Host = dto.Host;
        Success = dto.Success;
        Json = dto.Json;
    }

    public string WhenLocal { get; }
    public string Module { get; }
    public string Collection { get; }
    public string Host { get; }
    public bool Success { get; }
    public string Status => Success ? "SENT" : "FAILED";
    public string Json { get; }

    /// <summary>Stable key for preserving the selection across a refresh.</summary>
    public string Key => WhenLocal + "" + Module + "" + Collection + "" + Json.Length;
}

/// <summary>
/// "Recent records" tab: the last N record documents the collector shipped (or
/// failed to ship), newest first, from the service's in-memory ring buffer via the
/// <c>recent-records</c> control command. Selecting a row shows the exact JSON sent.
/// </summary>
public sealed class RecentRecordsPageViewModel : PageViewModelBase
{
    private const int MaxRecords = 200;
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(3);

    private readonly LocalCollectorAdminClient _adminClient;
    private readonly Action<string, bool> _statusCallback;
    private readonly Func<string, Task> _copyToClipboardAsync;

    private RecentRecordItemViewModel? _selectedRecord;
    private string _statusText = "Waiting for refresh.";
    private bool _autoRefresh;
    private CancellationTokenSource? _autoRefreshCts;

    public RecentRecordsPageViewModel(
        LocalCollectorAdminClient adminClient,
        Action<string, bool> statusCallback,
        Func<string, Task> copyToClipboardAsync)
        : base("Recent records")
    {
        _adminClient = adminClient;
        _statusCallback = statusCallback;
        _copyToClipboardAsync = copyToClipboardAsync;

        Records = new ObservableCollection<RecentRecordItemViewModel>();
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        CopyJsonCommand = new AsyncRelayCommand(CopyJsonAsync);
    }

    public ObservableCollection<RecentRecordItemViewModel> Records { get; }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand CopyJsonCommand { get; }

    public RecentRecordItemViewModel? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (SetProperty(ref _selectedRecord, value))
            {
                NotifyPropertyChanged(nameof(SelectedJson));
            }
        }
    }

    /// <summary>The exact JSON document the collector sent for the selected row.</summary>
    public string SelectedJson => _selectedRecord?.Json ?? string.Empty;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool AutoRefresh
    {
        get => _autoRefresh;
        set
        {
            if (!SetProperty(ref _autoRefresh, value)) return;
            if (value) StartAutoRefresh();
            else StopAutoRefresh();
        }
    }

    public override async Task RefreshAsync()
    {
        IReadOnlyList<RecentRecordDto> records;
        try
        {
            records = await _adminClient.GetRecentRecordsAsync(MaxRecords);
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() => StatusText = $"Recent records load failed: {ex.Message}");
            return;
        }

        await RunOnUiThreadAsync(() => Apply(records));
    }

    private void Apply(IReadOnlyList<RecentRecordDto> records)
    {
        // Preserve the selection across the refresh (rows are rebuilt each cycle).
        var selectedKey = _selectedRecord?.Key;

        Records.Clear();
        RecentRecordItemViewModel? reselect = null;
        foreach (var dto in records)
        {
            var item = new RecentRecordItemViewModel(dto);
            Records.Add(item);
            if (selectedKey != null && reselect == null && item.Key == selectedKey)
            {
                reselect = item;
            }
        }

        SelectedRecord = reselect;
        StatusText = Records.Count == 0
            ? "No records captured yet. They appear here as the collector ships data."
            : $"Showing the {Records.Count} most recent record(s), newest first.";
    }

    private async Task CopyJsonAsync()
    {
        if (string.IsNullOrEmpty(SelectedJson)) return;
        await _copyToClipboardAsync(SelectedJson);
        _statusCallback("Record JSON copied to clipboard.", true);
    }

    private void StartAutoRefresh()
    {
        if (_autoRefreshCts is { IsCancellationRequested: false }) return;
        _autoRefreshCts = new CancellationTokenSource();
        var token = _autoRefreshCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await RefreshAsync();
                    await Task.Delay(AutoRefreshInterval, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch
                {
                    try { await Task.Delay(AutoRefreshInterval, token); }
                    catch (TaskCanceledException) { break; }
                }
            }
        }, token);
    }

    private void StopAutoRefresh()
    {
        _autoRefreshCts?.Cancel();
        _autoRefreshCts?.Dispose();
        _autoRefreshCts = null;
    }

    private static Task RunOnUiThreadAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }
}
