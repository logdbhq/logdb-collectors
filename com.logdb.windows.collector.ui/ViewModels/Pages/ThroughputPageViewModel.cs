using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
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

    // User colour overrides keyed by series name (e.g. "EventLog · sent"), loaded
    // from and saved back to user-settings.json so picks survive a restart.
    private readonly Dictionary<string, SKColor> _colorOverrides;

    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(10);

    public ThroughputPageViewModel(LocalCollectorAdminClient adminClient, Action<string, bool> statusCallback)
        : base("Throughput")
    {
        _adminClient = adminClient;
        _statusCallback = statusCallback;

        Modules = new ObservableCollection<string> { AllOption };
        Hosts = new ObservableCollection<string> { AllOption };
        Collections = new ObservableCollection<string> { AllOption };
        LegendItems = new ObservableCollection<ThroughputSeriesLegendItem>();

        _colorOverrides = LoadColorOverrides();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ClearStatsCommand = new AsyncRelayCommand(ClearStatsAsync);

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

    /// <summary>Custom legend rows with clickable colour swatches (the built-in
    /// LiveCharts legend can't be recoloured by the user).</summary>
    public ObservableCollection<ThroughputSeriesLegendItem> LegendItems { get; }

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
    public AsyncRelayCommand ClearStatsCommand { get; }

    private async Task ClearStatsAsync()
    {
        var (success, message) = await _adminClient.ResetSendActivityAsync();
        _statusCallback(success ? "Send statistics cleared." : message, success);
        if (success)
        {
            await RefreshAsync();
        }
    }

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
        // UnitWidth tells the column series how wide one bucket is on a continuous
        // time axis; without it columns default to a 1-tick width and render as
        // hairline spikes. MinStep stops labels bunching up at fine zoom.
        var unitTicks = (Granularity == SendActivityGranularity.Day
            ? TimeSpan.FromDays(1)
            : TimeSpan.FromHours(1)).Ticks;
        XAxes = new[]
        {
            new Axis
            {
                LabelsPaint = new SolidColorPaint(new SKColor(0x90, 0x90, 0x90)),
                TextSize = 11,
                Labeler = FormatTick,
                UnitWidth = unitTicks,
                MinStep = unitTicks
            }
        };

        SyncOptions(Modules, data.Modules);
        SyncOptions(Hosts, data.Hosts);
        SyncOptions(Collections, data.Collections);

        TotalSent = data.TotalSent;
        TotalFailed = data.TotalFailed;

        // Stacked columns: every (group, sent/failed) pair is its own segment, all in
        // the default stack group, so a bucket's bar = total throughput for that time,
        // split into individually-coloured segments that each map to a legend swatch.
        var series = new List<ISeries>();
        var legend = new List<ThroughputSeriesLegendItem>();
        var multi = data.Series.Count > 1;

        foreach (var s in data.Series)
        {
            if (ShowSent)
            {
                var name = multi ? $"{s.Name} · sent" : "sent";
                var color = ResolveColor(name, ColorFor(s.Name));
                series.Add(new StackedColumnSeries<DateTimePoint>
                {
                    Name = name,
                    Values = s.Buckets.Select(b => new DateTimePoint(b.StartUtc.ToLocalTime(), b.Sent)).ToArray(),
                    Fill = new SolidColorPaint(color),
                    Stroke = null,
                    Padding = 1
                });
                legend.Add(BuildLegendItem(name, color));
            }
            if (ShowFailed)
            {
                var name = multi ? $"{s.Name} · failed" : "failed";
                var color = ResolveColor(name, FailedColorFor(s.Name));
                series.Add(new StackedColumnSeries<DateTimePoint>
                {
                    Name = name,
                    Values = s.Buckets.Select(b => new DateTimePoint(b.StartUtc.ToLocalTime(), b.Failed)).ToArray(),
                    Fill = new SolidColorPaint(color),
                    Stroke = null,
                    Padding = 1
                });
                legend.Add(BuildLegendItem(name, color));
            }
        }

        Series = series.ToArray();

        // Only rebuild the legend when the set of series changed — otherwise an
        // auto-refresh would tear down items and close any open colour picker.
        if (!LegendItems.Select(i => i.Name).SequenceEqual(legend.Select(i => i.Name)))
        {
            LegendItems.Clear();
            foreach (var item in legend) LegendItems.Add(item);
        }

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

    // Fixed palettes + a stable (process-independent) hash so a given series keeps
    // the same colour across auto-refreshes and restarts.
    private static readonly SKColor[] SentPalette =
    {
        new(0x42, 0xA5, 0xF5), new(0x66, 0xBB, 0x6A), new(0xFF, 0xA7, 0x26),
        new(0xAB, 0x47, 0xBC), new(0x26, 0xC6, 0xDA), new(0xFF, 0xCA, 0x28),
        new(0x8D, 0x6E, 0x63), new(0x78, 0x90, 0x9C),
    };

    private static readonly SKColor[] FailedPalette =
    {
        new(0xE5, 0x39, 0x35), new(0xD8, 0x1B, 0x60), new(0xC6, 0x28, 0x28),
        new(0xF4, 0x51, 0x1E), new(0xAD, 0x14, 0x57),
    };

    private static SKColor ColorFor(string key) => SentPalette[StableIndex(key, SentPalette.Length)];

    private static SKColor FailedColorFor(string key) => FailedPalette[StableIndex(key, FailedPalette.Length)];

    // ── Colour overrides (adjustable + persisted) ─────────────────────────

    private SKColor ResolveColor(string seriesName, SKColor fallback) =>
        _colorOverrides.TryGetValue(seriesName, out var c) ? c : fallback;

    private ThroughputSeriesLegendItem BuildLegendItem(string name, SKColor color)
    {
        var item = new ThroughputSeriesLegendItem(name, ToAvalonia(color));
        item.ColorChanged += OnLegendColorChanged;
        return item;
    }

    // The user picked a new colour from a legend swatch: recolour the live series
    // segment in place (no full rebuild → flyout stays open), remember and persist it.
    private void OnLegendColorChanged(ThroughputSeriesLegendItem item)
    {
        var sk = ToSk(item.Color);
        _colorOverrides[item.Name] = sk;

        foreach (var s in _series)
        {
            if (s.Name == item.Name && s is StackedColumnSeries<DateTimePoint> col)
            {
                col.Fill = new SolidColorPaint(sk);
                break;
            }
        }

        PersistColorOverrides();
    }

    private Dictionary<string, SKColor> LoadColorOverrides()
    {
        var result = new Dictionary<string, SKColor>();
        foreach (var (name, hex) in WindowPlacementStore.LoadThroughputColors())
        {
            if (TryParseHex(hex, out var c)) result[name] = c;
        }
        return result;
    }

    private void PersistColorOverrides()
    {
        try
        {
            WindowPlacementStore.SaveThroughputColors(
                _colorOverrides.ToDictionary(kv => kv.Key, kv => ToHex(kv.Value)));
        }
        catch
        {
            // Colour persistence is best-effort; a failed save must not break the chart.
        }
    }

    private static SKColor ToSk(Color c) => new(c.R, c.G, c.B, c.A);

    private static Color ToAvalonia(SKColor c) => Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);

    private static string ToHex(SKColor c) => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

    private static bool TryParseHex(string? hex, out SKColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.TrimStart('#');
        if (s.Length != 6) return false;
        if (!byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return false;
        }
        color = new SKColor(r, g, b);
        return true;
    }

    private static int StableIndex(string key, int mod)
    {
        unchecked
        {
            var h = 17;
            foreach (var c in key) h = h * 31 + c;
            return ((h % mod) + mod) % mod;
        }
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
