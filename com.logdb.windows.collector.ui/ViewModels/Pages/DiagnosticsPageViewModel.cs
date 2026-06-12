using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Media;
using Avalonia.Threading;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.ui.Services;
using com.logdb.windows.collector.ui.ViewModels.Infrastructure;

namespace com.logdb.windows.collector.ui.ViewModels.Pages;

public sealed class OnlineDiagnosticRowViewModel
{
    private static readonly IBrush EventLogBrush = new SolidColorBrush(Color.Parse("#81C784"));
    private static readonly IBrush IisBrush = new SolidColorBrush(Color.Parse("#4FC3F7"));
    private static readonly IBrush MetricsBrush = new SolidColorBrush(Color.Parse("#FFB74D"));
    private static readonly IBrush HeartbeatBrush = new SolidColorBrush(Color.Parse("#BA68C8"));

    public string TimeLocal { get; set; } = string.Empty;
    public string EventTimeLocal { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public IBrush? RowForeground { get; set; }
    public AsyncRelayCommand? CopyCommand { get; set; }

    /// <summary>
    /// First line of <see cref="Message"/> for grid display. Exception stack
    /// traces are appended to the message by the collector's logger; in a grid
    /// cell they render crushed, so the grid shows the headline and the Errors
    /// tab's detail pane shows the full text.
    /// </summary>
    public string MessageFirstLine
    {
        get
        {
            var idx = Message.IndexOfAny(new[] { '\r', '\n' });
            return idx < 0 ? Message : Message[..idx] + "  …";
        }
    }

    /// <summary>True for levels that belong on the Errors tab.</summary>
    public bool IsErrorLike =>
        Level.Equals("Warning", StringComparison.OrdinalIgnoreCase)
        || Level.Equals("Error", StringComparison.OrdinalIgnoreCase)
        || Level.Equals("Critical", StringComparison.OrdinalIgnoreCase);

    public string ToLogLine() =>
        $"[{TimeLocal}] [{Level}] {Module}: {Message}";

    public static (string Module, IBrush? Brush) ResolveModule(string category)
    {
        if (category.Contains(".eventviewer.", StringComparison.OrdinalIgnoreCase))
            return ("EventLog", EventLogBrush);
        if (category.Contains(".iis.", StringComparison.OrdinalIgnoreCase))
            return ("IIS", IisBrush);
        if (category.Contains(".tracker.", StringComparison.OrdinalIgnoreCase))
            return ("Metrics", MetricsBrush);
        if (category.Contains("Heartbeat", StringComparison.OrdinalIgnoreCase))
            return ("Heartbeat", HeartbeatBrush);

        var shortName = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
        return (shortName, null);
    }
}

public sealed class OnlineModuleFilterItemViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private bool _isSelected;

    public OnlineModuleFilterItemViewModel(string name, Action onChanged)
    {
        Name = name;
        _onChanged = onChanged;
    }

    public string Name { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _onChanged();
            }
        }
    }
}

public sealed class DiagnosticsPageViewModel : PageViewModelBase
{
    private readonly LocalCollectorAdminClient _adminClient;
    private readonly Action<string, bool> _statusCallback;
    private readonly Func<string, string, Task<bool>> _exportTextAsync;
    private readonly Func<string, Task> _copyToClipboardAsync;

    private bool _onlineAutoRefresh = true;
    private int _onlineTailCount = 120;
    private string _onlineStatusText = "Waiting for refresh.";
    private string _onlineModuleFilterSummary = OnlineModuleFilterAll;
    private CancellationTokenSource? _onlineLoopCts;
    private readonly SemaphoreSlim _onlineRefreshLock = new(1, 1);
    private readonly List<OnlineDiagnosticRowViewModel> _allOnlineRows = new();
    private static readonly HashSet<string> KnownModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "EventLog", "IIS", "Metrics", "Heartbeat"
    };

    public const string OnlineModuleFilterAll = "(All)";
    public const string OnlineModuleFilterOther = "Other";

    /// <summary>Second sub-tab of the Online Console: send throughput charts.</summary>
    public ThroughputPageViewModel Throughput { get; }

    public DiagnosticsPageViewModel(
        LocalCollectorAdminClient adminClient,
        Action<string, bool> statusCallback,
        Func<string, string, Task<bool>> exportTextAsync,
        Func<string, Task> copyToClipboardAsync)
        : base("Diagnostics")
    {
        _adminClient = adminClient;
        _statusCallback = statusCallback;
        _exportTextAsync = exportTextAsync;
        _copyToClipboardAsync = copyToClipboardAsync;
        Throughput = new ThroughputPageViewModel(adminClient, statusCallback);

        OnlineConsoleRows = new ObservableCollection<OnlineDiagnosticRowViewModel>();
        OnlineModuleFilters = new ObservableCollection<OnlineModuleFilterItemViewModel>
        {
            new("EventLog", OnFilterSelectionChanged),
            new("IIS", OnFilterSelectionChanged),
            new("Metrics", OnFilterSelectionChanged),
            new("Heartbeat", OnFilterSelectionChanged),
            new(OnlineModuleFilterOther, OnFilterSelectionChanged)
        };

        ExportDiagnosticsCommand = new AsyncRelayCommand(ExportDiagnosticsAsync);
        CopySupportBundleCommand = new AsyncRelayCommand(CopySupportBundleAsync);
        RefreshOnlineConsoleCommand = new AsyncRelayCommand(RefreshOnlineConsoleAsync);
        ClearOnlineConsoleCommand = new RelayCommand(ClearOnlineConsole);

        ErrorRows = new ObservableCollection<OnlineDiagnosticRowViewModel>();
        CopyErrorDetailCommand = new AsyncRelayCommand(async () =>
        {
            if (!string.IsNullOrEmpty(SelectedErrorDetail))
            {
                await _copyToClipboardAsync(SelectedErrorDetail);
                _statusCallback("Error detail copied to clipboard.", true);
            }
        });
    }

    // ── Errors sub-tab: warnings/errors only, with full-detail pane ────────

    /// <summary>Warning/Error/Critical rows from the live tail, newest first.</summary>
    public ObservableCollection<OnlineDiagnosticRowViewModel> ErrorRows { get; }

    public AsyncRelayCommand CopyErrorDetailCommand { get; }

    private OnlineDiagnosticRowViewModel? _selectedErrorRow;
    public OnlineDiagnosticRowViewModel? SelectedErrorRow
    {
        get => _selectedErrorRow;
        set
        {
            if (SetProperty(ref _selectedErrorRow, value))
            {
                NotifyPropertyChanged(nameof(SelectedErrorDetail));
            }
        }
    }

    /// <summary>Full multi-line text (incl. stack trace) of the selected error.</summary>
    public string SelectedErrorDetail => _selectedErrorRow?.Message ?? string.Empty;

    private string _errorsTabHeader = "Errors";
    public string ErrorsTabHeader
    {
        get => _errorsTabHeader;
        private set => SetProperty(ref _errorsTabHeader, value);
    }

    /// <summary>
    /// Rebuilds the Errors tab from the freshly fetched tail. Ignores the module
    /// filter on purpose — an error should never be hidden by a view filter.
    /// Keeps the current selection when the same entry is still present.
    /// </summary>
    private void UpdateErrorRows()
    {
        var selectedKey = _selectedErrorRow is { } sel ? sel.TimeLocal + "" + sel.Message : null;

        ErrorRows.Clear();
        OnlineDiagnosticRowViewModel? reselect = null;
        foreach (var row in _allOnlineRows.Where(r => r.IsErrorLike))
        {
            ErrorRows.Add(row);
            if (selectedKey != null && reselect == null
                && row.TimeLocal + "" + row.Message == selectedKey)
            {
                reselect = row;
            }
        }

        ErrorsTabHeader = ErrorRows.Count == 0 ? "Errors" : $"Errors ({ErrorRows.Count})";
        if (reselect != null)
        {
            SelectedErrorRow = reselect;
        }
    }

    public ObservableCollection<OnlineDiagnosticRowViewModel> OnlineConsoleRows { get; }

    public bool OnlineAutoRefresh
    {
        get => _onlineAutoRefresh;
        set
        {
            if (!SetProperty(ref _onlineAutoRefresh, value))
            {
                return;
            }

            if (value)
            {
                EnsureOnlineLoopStarted();
            }
            else
            {
                StopOnlineLoop();
            }
        }
    }

    public int OnlineTailCount
    {
        get => _onlineTailCount;
        set => SetProperty(ref _onlineTailCount, value);
    }

    public string OnlineStatusText
    {
        get => _onlineStatusText;
        set => SetProperty(ref _onlineStatusText, value);
    }

    public ObservableCollection<OnlineModuleFilterItemViewModel> OnlineModuleFilters { get; }

    public string OnlineModuleFilterSummary
    {
        get => _onlineModuleFilterSummary;
        private set => SetProperty(ref _onlineModuleFilterSummary, value);
    }

    private void OnFilterSelectionChanged()
    {
        UpdateOnlineModuleFilterSummary();
        ApplyOnlineFilter();
    }

    private void UpdateOnlineModuleFilterSummary()
    {
        var selected = OnlineModuleFilters.Where(f => f.IsSelected).Select(f => f.Name).ToList();
        OnlineModuleFilterSummary = selected.Count == 0 || selected.Count == OnlineModuleFilters.Count
            ? OnlineModuleFilterAll
            : string.Join(", ", selected);
    }

    public AsyncRelayCommand ExportDiagnosticsCommand { get; }
    public AsyncRelayCommand CopySupportBundleCommand { get; }
    public AsyncRelayCommand RefreshOnlineConsoleCommand { get; }
    public RelayCommand ClearOnlineConsoleCommand { get; }

    public override async Task RefreshAsync()
    {
        await RefreshOnlineConsoleAsync();
        if (OnlineAutoRefresh)
        {
            EnsureOnlineLoopStarted();
        }
    }

    private async Task RefreshOnlineConsoleAsync()
    {
        if (_adminClient.SelectedTarget == null)
        {
            await RunOnUiThreadAsync(() =>
            {
                _allOnlineRows.Clear();
                OnlineConsoleRows.Clear();
                OnlineStatusText = "No local collector target selected. Start service or console mode.";
            });
            return;
        }

        if (!await _onlineRefreshLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            var diagnostics = (await _adminClient.GetDiagnosticsAsync(Math.Clamp(OnlineTailCount, 20, 500)))
                .OrderByDescending(line => line.TimestampUtc)
                .ToList();

            await RunOnUiThreadAsync(() =>
            {
                _allOnlineRows.Clear();
                foreach (var line in diagnostics)
                {
                    var (module, brush) = OnlineDiagnosticRowViewModel.ResolveModule(line.Category);
                    var row = new OnlineDiagnosticRowViewModel
                    {
                        TimeLocal = line.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                        EventTimeLocal = line.EventTimestampUtc.HasValue
                            ? line.EventTimestampUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                            : string.Empty,
                        Level = line.Level,
                        Module = module,
                        Message = line.Message,
                        RowForeground = brush
                    };
                    row.CopyCommand = new AsyncRelayCommand(() => CopyRowAsync(row));
                    _allOnlineRows.Add(row);
                }

                ApplyOnlineFilter();
            });
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() =>
            {
                OnlineStatusText = $"Live tail refresh failed: {ex.Message}";
            });
        }
        finally
        {
            _onlineRefreshLock.Release();
        }
    }

    private void ApplyOnlineFilter()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            _ = RunOnUiThreadAsync(ApplyOnlineFilter);
            return;
        }

        var selectedNames = OnlineModuleFilters
            .Where(f => f.IsSelected)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Zero selected or every option selected = no filter (show all).
        var filterAll = selectedNames.Count == 0 || selectedNames.Count == OnlineModuleFilters.Count;

        IEnumerable<OnlineDiagnosticRowViewModel> visible = filterAll
            ? _allOnlineRows
            : _allOnlineRows.Where(r => MatchesSelectedFilters(r.Module, selectedNames));

        OnlineConsoleRows.Clear();
        foreach (var row in visible)
        {
            OnlineConsoleRows.Add(row);
        }

        UpdateErrorRows();

        var suffix = filterAll
            ? string.Empty
            : $"  (filtered by source: {string.Join(", ", selectedNames)}, {_allOnlineRows.Count - OnlineConsoleRows.Count} hidden)";
        OnlineStatusText = $"Live tail loaded: {OnlineConsoleRows.Count} line(s).{suffix}";
    }

    private static bool MatchesSelectedFilters(string module, HashSet<string> selected)
    {
        if (selected.Contains(module))
        {
            return true;
        }
        // "Other" catches anything that isn't one of the known well-known module names.
        return selected.Contains(OnlineModuleFilterOther) && !KnownModules.Contains(module);
    }

    private void ClearOnlineConsole()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            _ = RunOnUiThreadAsync(ClearOnlineConsole);
            return;
        }

        _allOnlineRows.Clear();
        OnlineConsoleRows.Clear();
        OnlineStatusText = "Live tail cleared.";
    }

    private void EnsureOnlineLoopStarted()
    {
        if (_onlineLoopCts != null && !_onlineLoopCts.IsCancellationRequested)
        {
            return;
        }

        _onlineLoopCts = new CancellationTokenSource();
        var token = _onlineLoopCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await RefreshOnlineConsoleAsync();
                    await Task.Delay(TimeSpan.FromSeconds(3), token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), token);
                }
            }
        }, token);
    }

    private void StopOnlineLoop()
    {
        if (_onlineLoopCts == null)
        {
            return;
        }

        try
        {
            _onlineLoopCts.Cancel();
        }
        finally
        {
            _onlineLoopCts.Dispose();
            _onlineLoopCts = null;
        }
    }

    private async Task ExportDiagnosticsAsync()
    {
        // Export everything currently in the buffer — what you see is what you get.
        var snapshot = _allOnlineRows.ToList();
        var text = string.Join(Environment.NewLine, snapshot.Select(r => r.ToLogLine()));

        var exported = await _exportTextAsync("collector-diagnostics.txt", text);
        OnlineStatusText = exported
            ? $"Exported {snapshot.Count} diagnostic line(s)."
            : "Diagnostics export was canceled.";
        _statusCallback(OnlineStatusText, exported);
    }

    private async Task CopySupportBundleAsync()
    {
        var bundle = await _adminClient.BuildSupportBundleAsync(200);
        await _copyToClipboardAsync(bundle);
        OnlineStatusText = "Support bundle copied to clipboard.";
        _statusCallback(OnlineStatusText, true);
    }

    private async Task CopyRowAsync(OnlineDiagnosticRowViewModel row)
    {
        await _copyToClipboardAsync(row.ToLogLine());
        OnlineStatusText = "Diagnostic line copied to clipboard.";
        _statusCallback(OnlineStatusText, true);
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
