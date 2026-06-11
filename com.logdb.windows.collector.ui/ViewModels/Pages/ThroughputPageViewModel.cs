using System.Collections.ObjectModel;
using Avalonia.Threading;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.ui.Services;
using com.logdb.windows.collector.ui.ViewModels.Infrastructure;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace com.logdb.windows.collector.ui.ViewModels.Pages;

/// <summary>
/// "Throughput" tab: charts how many records each module shipped over a selected
/// date range, split sent vs failed, grouped by module / host / collection.
/// Data comes from the service's persisted SendActivityTracker via the
/// <c>send-activity</c> control command.
/// </summary>
public sealed class ThroughputPageViewModel : PageViewModelBase
{
    public const string AllOption = "All";

    private readonly LocalCollectorAdminClient _adminClient;
    private readonly Action<string, bool> _statusCallback;

    private int _rangeDays = 7;
    private SendActivityGranularity _granularity = SendActivityGranularity.Hour;
    private SendActivityGroupBy _groupBy = SendActivityGroupBy.Module;
    private bool _showSent = true;
    private bool _showFailed = true;
    private string _moduleFilter = AllOption;
    private string _hostFilter = AllOption;
    private string _collectionFilter = AllOption;

    private ISeries[] _series = Array.Empty<ISeries>();
    private Axis[] _xAxes;
    private Axis[] _yAxes;
    private long _totalSent;
    private long _totalFailed;
    private bool _suppressReload;
    private bool _autoRefresh;
    private CancellationTokenSource? _autoRefreshCts;

    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(10);

    public ThroughputPageViewModel(LocalCollectorAdminClient adminClient, Action<string, bool> statusCallback)
        : base("Throughput")
    {
        _adminClient = adminClient;
        _statusCallback = statusCallback;

        Modules = new ObservableCollection<string> { AllOption };
        Hosts = new ObservableCollection<string> { AllOption };
        Collections = new ObservableCollection<string> { AllOption };

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);

        var axisPaint = new SolidColorPaint(new SKColor(0x90, 0x90, 0x90));
        _xAxes = new[] { new Axis { LabelsPaint = axisPaint, TextSize = 11, LabelsRotation = 0, Labeler = FormatTick } };
        _yAxes = new[] { new Axis { LabelsPaint = axisPaint, TextSize = 11, MinLimit = 0, Name = "records", NamePaint = axisPaint } };
    }

    // ── Range / grouping options ──────────────────────────────────────────

    public IReadOnlyList<int> RangeDayOptions { get; } = new[] { 1, 7, 30, 90 };
    public IReadOnlyList<SendActivityGranularity> Granularities { get; } =
        new[] { SendActivityGranularity.Hour, SendActivityGranularity.Day };
    public IReadOnlyList<SendActivityGroupBy> GroupByOptions { get; } =
        new[] { SendActivityGroupBy.Module, SendActivityGroupBy.Host, SendActivityGroupBy.Collection, SendActivityGroupBy.None };

    public int RangeDays
    {
        get => _rangeDays;
        set { if (SetProperty(ref _rangeDays, value)) Reload(); }
    }

    public SendActivityGranularity Granularity
    {
        get => _granularity;
        set { if (SetProperty(ref _granularity, value)) Reload(); }
    }

    public SendActivityGroupBy GroupBy
    {
        get => _groupBy;
        set { if (SetProperty(ref _groupBy, value)) Reload(); }
    }

    public bool ShowSent
    {
        get => _showSent;
        set { if (SetProperty(ref _showSent, value)) Reload(); }
    }

    public bool ShowFailed
    {
        get => _showFailed;
        set { if (SetProperty(ref _showFailed, value)) Reload(); }
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

    public ObservableCollection<string> Modules { get; }
    public ObservableCollection<string> Hosts { get; }
    public ObservableCollection<string> Collections { get; }

    public string ModuleFilter
    {
        get => _moduleFilter;
        set { if (SetProperty(ref _moduleFilter, value)) Reload(); }
    }

    public string HostFilter
    {
        get => _hostFilter;
        set { if (SetProperty(ref _hostFilter, value)) Reload(); }
    }

    public string CollectionFilter
    {
        get => _collectionFilter;
        set { if (SetProperty(ref _collectionFilter, value)) Reload(); }
    }

    // ── Chart bindings ────────────────────────────────────────────────────

    public ISeries[] Series
    {
        get => _series;
        private set => SetProperty(ref _series, value);
    }

    public Axis[] XAxes
    {
        get => _xAxes;
        private set => SetProperty(ref _xAxes, value);
    }

    public Axis[] YAxes
    {
        get => _yAxes;
        private set => SetProperty(ref _yAxes, value);
    }

    public long TotalSent
    {
        get => _totalSent;
        private set => SetProperty(ref _totalSent, value);
    }

    public long TotalFailed
    {
        get => _totalFailed;
        private set => SetProperty(ref _totalFailed, value);
    }

    public AsyncRelayCommand RefreshCommand { get; }

    private void Reload()
    {
        if (_suppressReload) return;
        _ = RefreshAsync();
    }

    public override async Task RefreshAsync()
    {
        SendActivityDto? data;
        try
        {
            var now = DateTime.UtcNow;
            var query = new SendActivityQueryDto
            {
                FromUtc = now.AddDays(-Math.Max(1, RangeDays)),
                ToUtc = now,
                Granularity = Granularity,
                GroupBy = GroupBy,
                Module = NormFilter(ModuleFilter),
                Host = NormFilter(HostFilter),
                Collection = NormFilter(CollectionFilter)
            };

            data = await _adminClient.GetSendActivityAsync(query);
        }
        catch (Exception ex)
        {
            _statusCallback($"Throughput load failed: {ex.Message}", false);
            return;
        }

        // The auto-refresh loop runs on a background thread, but the chart series
        // and axis bindings are UI-thread-affine — apply on the UI thread.
        if (Dispatcher.UIThread.CheckAccess())
            Apply(data);
        else
            await Dispatcher.UIThread.InvokeAsync(() => Apply(data));
    }

    private void Apply(SendActivityDto? data)
    {
        if (data == null)
        {
            Series = Array.Empty<ISeries>();
            TotalSent = 0;
            TotalFailed = 0;
            _statusCallback("Throughput: collector not reachable.", false);
            return;
        }

        // Keep the time-axis label format in step with the chosen granularity.
        XAxes = new[]
        {
            new Axis
            {
                LabelsPaint = new SolidColorPaint(new SKColor(0x90, 0x90, 0x90)),
                TextSize = 11,
                Labeler = FormatTick
            }
        };

        SyncOptions(Modules, data.Modules);
        SyncOptions(Hosts, data.Hosts);
        SyncOptions(Collections, data.Collections);

        TotalSent = data.TotalSent;
        TotalFailed = data.TotalFailed;

        var series = new List<ISeries>();
        foreach (var s in data.Series)
        {
            if (ShowSent)
            {
                series.Add(new LineSeries<DateTimePoint>
                {
                    Name = data.Series.Count > 1 ? $"{s.Name} · sent" : "sent",
                    Values = s.Buckets.Select(b => new DateTimePoint(b.StartUtc.ToLocalTime(), b.Sent)).ToArray(),
                    GeometrySize = 0,
                    LineSmoothness = 0.2
                });
            }
            if (ShowFailed)
            {
                series.Add(new LineSeries<DateTimePoint>
                {
                    Name = data.Series.Count > 1 ? $"{s.Name} · failed" : "failed",
                    Values = s.Buckets.Select(b => new DateTimePoint(b.StartUtc.ToLocalTime(), b.Failed)).ToArray(),
                    GeometrySize = 0,
                    LineSmoothness = 0.2,
                    Stroke = new SolidColorPaint(new SKColor(0xE5, 0x39, 0x35), 2),
                    Fill = null
                });
            }
        }

        Series = series.ToArray();

        if (TotalSent == 0 && TotalFailed == 0)
            _statusCallback("Throughput: no send activity recorded yet for this range.", true);
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

    private string FormatTick(double ticks)
    {
        var dt = new DateTime((long)Math.Max(0, ticks), DateTimeKind.Utc).ToLocalTime();
        return Granularity == SendActivityGranularity.Day ? dt.ToString("MMM d") : dt.ToString("MMM d HH:mm");
    }

    private static string? NormFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == AllOption ? null : value;

    private void SyncOptions(ObservableCollection<string> target, List<string> values)
    {
        var desired = new List<string> { AllOption };
        desired.AddRange(values);
        if (target.SequenceEqual(desired)) return;

        var keepSelection = target.Count > 0;
        _suppressReload = true;
        try
        {
            target.Clear();
            foreach (var v in desired) target.Add(v);
        }
        finally
        {
            _suppressReload = false;
        }
    }
}
