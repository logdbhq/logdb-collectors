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

    public string TimeLocal { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public IBrush? RowForeground { get; set; }

    public static (string Module, IBrush? Brush) ResolveModule(string category)
    {
        if (category.Contains(".eventviewer.", StringComparison.OrdinalIgnoreCase))
            return ("EventLog", EventLogBrush);
        if (category.Contains(".iis.", StringComparison.OrdinalIgnoreCase))
            return ("IIS", IisBrush);
        if (category.Contains(".tracker.", StringComparison.OrdinalIgnoreCase))
            return ("Metrics", MetricsBrush);

        var shortName = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
        return (shortName, null);
    }
}

public sealed class DiagnosticsPageViewModel : PageViewModelBase
{
    private readonly LocalCollectorAdminClient _adminClient;
    private readonly Action<string, bool> _statusCallback;
    private readonly Func<string, string, Task<bool>> _exportTextAsync;
    private readonly Func<string, Task> _copyToClipboardAsync;

    private int _tailCount = 200;
    private string _statusText = "-";
    private bool _onlineAutoRefresh = true;
    private int _onlineTailCount = 120;
    private string _onlineStatusText = "Waiting for refresh.";
    private string _onlineModuleFilter = OnlineModuleFilterAll;
    private CancellationTokenSource? _onlineLoopCts;
    private readonly SemaphoreSlim _onlineRefreshLock = new(1, 1);
    private readonly List<OnlineDiagnosticRowViewModel> _allOnlineRows = new();

    public const string OnlineModuleFilterAll = "(All)";
    public const string OnlineModuleFilterOther = "Other";

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

        DiagnosticLines = new ObservableCollection<DiagnosticLineItemViewModel>();
        OnlineConsoleRows = new ObservableCollection<OnlineDiagnosticRowViewModel>();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ExportDiagnosticsCommand = new AsyncRelayCommand(ExportDiagnosticsAsync);
        CopySupportBundleCommand = new AsyncRelayCommand(CopySupportBundleAsync);
        RefreshOnlineConsoleCommand = new AsyncRelayCommand(RefreshOnlineConsoleAsync);
        ClearOnlineConsoleCommand = new RelayCommand(ClearOnlineConsole);
    }

    public ObservableCollection<DiagnosticLineItemViewModel> DiagnosticLines { get; }
    public ObservableCollection<OnlineDiagnosticRowViewModel> OnlineConsoleRows { get; }

    public int TailCount
    {
        get => _tailCount;
        set => SetProperty(ref _tailCount, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

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

    public string OnlineModuleFilter
    {
        get => _onlineModuleFilter;
        set
        {
            if (SetProperty(ref _onlineModuleFilter, value ?? OnlineModuleFilterAll))
            {
                ApplyOnlineFilter();
            }
        }
    }

    public IReadOnlyList<string> OnlineModuleFilterOptions { get; } = new[]
    {
        OnlineModuleFilterAll,
        "EventLog",
        "IIS",
        "Metrics",
        OnlineModuleFilterOther
    };

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ExportDiagnosticsCommand { get; }
    public AsyncRelayCommand CopySupportBundleCommand { get; }
    public AsyncRelayCommand RefreshOnlineConsoleCommand { get; }
    public RelayCommand ClearOnlineConsoleCommand { get; }

    public override async Task RefreshAsync()
    {
        await RefreshRecentDiagnosticsAsync();
        await RefreshOnlineConsoleAsync();
        if (OnlineAutoRefresh)
        {
            EnsureOnlineLoopStarted();
        }
    }

    private async Task RefreshRecentDiagnosticsAsync()
    {
        var diagnostics = (await _adminClient.GetDiagnosticsAsync(Math.Clamp(TailCount, 1, 500)))
            .OrderByDescending(line => line.TimestampUtc)
            .ToList();

        await RunOnUiThreadAsync(() =>
        {
            DiagnosticLines.Clear();
            foreach (var line in diagnostics)
            {
                DiagnosticLines.Add(
                    new DiagnosticLineItemViewModel(
                        FormatDiagnosticLine(line),
                        _copyToClipboardAsync,
                        OnDiagnosticLineCopied));
            }

            StatusText = $"Loaded {DiagnosticLines.Count} diagnostic line(s).";
        });
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
                    _allOnlineRows.Add(new OnlineDiagnosticRowViewModel
                    {
                        TimeLocal = line.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                        Level = line.Level,
                        Module = module,
                        Message = line.Message,
                        RowForeground = brush
                    });
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

        var filter = _onlineModuleFilter ?? OnlineModuleFilterAll;
        IEnumerable<OnlineDiagnosticRowViewModel> visible = _allOnlineRows;

        if (!string.Equals(filter, OnlineModuleFilterAll, StringComparison.Ordinal))
        {
            if (string.Equals(filter, OnlineModuleFilterOther, StringComparison.Ordinal))
            {
                visible = _allOnlineRows.Where(r =>
                    !string.Equals(r.Module, "EventLog", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(r.Module, "IIS", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(r.Module, "Metrics", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                visible = _allOnlineRows.Where(r => string.Equals(r.Module, filter, StringComparison.OrdinalIgnoreCase));
            }
        }

        OnlineConsoleRows.Clear();
        foreach (var row in visible)
        {
            OnlineConsoleRows.Add(row);
        }

        var suffix = string.Equals(filter, OnlineModuleFilterAll, StringComparison.Ordinal)
            ? string.Empty
            : $"  (filtered by source: {filter}, {_allOnlineRows.Count - OnlineConsoleRows.Count} hidden)";
        OnlineStatusText = $"Live tail loaded: {OnlineConsoleRows.Count} line(s).{suffix}";
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
        var diagnostics = (await _adminClient.GetDiagnosticsAsync(Math.Clamp(TailCount, 1, 500)))
            .OrderByDescending(line => line.TimestampUtc)
            .ToList();
        var text = string.Join(
            Environment.NewLine,
            diagnostics.Select(FormatDiagnosticLine));

        var exported = await _exportTextAsync("collector-diagnostics.txt", text);
        StatusText = exported ? "Diagnostics exported." : "Diagnostics export was canceled.";
        _statusCallback(StatusText, exported);
    }

    private async Task CopySupportBundleAsync()
    {
        var bundle = await _adminClient.BuildSupportBundleAsync(200);
        await _copyToClipboardAsync(bundle);
        StatusText = "Support bundle copied to clipboard.";
        _statusCallback(StatusText, true);
    }

    private void OnDiagnosticLineCopied()
    {
        StatusText = "Diagnostic line copied to clipboard.";
        _statusCallback(StatusText, true);
    }

    private static string FormatDiagnosticLine(DiagnosticEntryDto line)
    {
        return $"[{line.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}] [{line.Level}] {line.Category}: {line.Message}";
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

    public sealed class DiagnosticLineItemViewModel
    {
        private readonly Func<string, Task> _copyToClipboardAsync;
        private readonly Action _copiedCallback;

        public DiagnosticLineItemViewModel(
            string text,
            Func<string, Task> copyToClipboardAsync,
            Action copiedCallback)
        {
            Text = text;
            _copyToClipboardAsync = copyToClipboardAsync;
            _copiedCallback = copiedCallback;
            CopyCommand = new AsyncRelayCommand(CopyAsync);
        }

        public string Text { get; }
        public AsyncRelayCommand CopyCommand { get; }

        private async Task CopyAsync()
        {
            await _copyToClipboardAsync(Text);
            _copiedCallback();
        }
    }
}
