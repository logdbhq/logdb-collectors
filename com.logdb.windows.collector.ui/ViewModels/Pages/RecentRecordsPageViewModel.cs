using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        Ip = RecordIpExtractor.Extract(dto.Json);
    }

    public string WhenLocal { get; }
    public string Module { get; }
    public string Collection { get; }
    public string Host { get; }
    public bool Success { get; }
    public string Status => Success ? "SENT" : "FAILED";
    public string Json { get; }

    /// <summary>Remote address carried by the record, when it has one — the
    /// IIS client IP, or the source address of a Windows security event.
    /// Empty for records with no network peer (metrics, heartbeat).</summary>
    public string Ip { get; }

    /// <summary>Stable key for preserving the selection across a refresh.</summary>
    public string Key => WhenLocal + "" + Module + "" + Collection + "" + Json.Length;
}

/// <summary>
/// Pulls the remote address out of a shipped record document.
///
/// Two passes, in order of trustworthiness:
/// 1. Named JSON properties — IIS ships <c>clientIp</c> in its attributes, so
///    this is exact whenever the module records the field.
/// 2. Labelled text inside the record — Windows security events (4624/4625/
///    RDP) carry the peer only inside the message body, as
///    "Source Network Address: 10.0.0.5" / "Client Address: ...".
///
/// Deliberately NOT done: a blind IP-shaped regex over the whole document.
/// Dotted quads are everywhere in log text (versions like 1.4.27.0 parse as
/// valid IPv4), so that would invent addresses that were never in the record.
/// An empty cell means "this record has no address we can prove", which is the
/// honest answer.
/// </summary>
internal static class RecordIpExtractor
{
    private static readonly HashSet<string> IpPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "clientIp", "client_ip", "c-ip", "cip",
        "ipAddress", "ip_address", "ip",
        "sourceIp", "source_ip", "sourceAddress", "sourceNetworkAddress",
        "remoteAddress", "remoteIp", "callerIp", "peerAddress"
    };

    /// <summary>Server-side addresses: only used when nothing better is found,
    /// so an IIS row without a client IP still shows something meaningful.</summary>
    private static readonly HashSet<string> FallbackPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "serverIp", "server_ip", "s-ip", "destinationIp", "localAddress"
    };

    private static readonly Regex LabelledAddress = new(
        @"(?:Source Network Address|Client Address|Source Address|Source IP|Client IP)\s*[:=]\s*(?<ip>[0-9A-Fa-f:.]{3,45})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Extract(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            var fallback = string.Empty;
            var found = ScanElement(document.RootElement, ref fallback, depth: 0);
            if (!string.IsNullOrEmpty(found)) return found;
            if (!string.IsNullOrEmpty(fallback)) return fallback;
        }
        catch (JsonException)
        {
            // Not JSON (or truncated) — fall through to the text scan.
        }

        var match = LabelledAddress.Match(json);
        return match.Success && IsUsableAddress(match.Groups["ip"].Value)
            ? Normalize(match.Groups["ip"].Value)
            : string.Empty;
    }

    private static string ScanElement(JsonElement element, ref string fallback, int depth)
    {
        // Records are shallow; the bound stops a pathological document from
        // costing more than the grid row is worth.
        if (depth > 8) return string.Empty;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString();
                        if (IsUsableAddress(value))
                        {
                            if (IpPropertyNames.Contains(property.Name))
                                return Normalize(value!);
                            if (fallback.Length == 0 && FallbackPropertyNames.Contains(property.Name))
                                fallback = Normalize(value!);
                        }

                        // Message-shaped values can still carry a labelled address.
                        if (fallback.Length == 0 && value is { Length: > 0 })
                        {
                            var labelled = LabelledAddress.Match(value);
                            if (labelled.Success && IsUsableAddress(labelled.Groups["ip"].Value))
                                return Normalize(labelled.Groups["ip"].Value);
                        }
                    }
                    else
                    {
                        var nested = ScanElement(property.Value, ref fallback, depth + 1);
                        if (!string.IsNullOrEmpty(nested)) return nested;
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = ScanElement(item, ref fallback, depth + 1);
                    if (!string.IsNullOrEmpty(nested)) return nested;
                }
                break;
        }

        return string.Empty;
    }

    /// <summary>IIS writes "-" for a missing field, and Windows security events
    /// write "::" / "-" when there is no network peer (local logon).</summary>
    private static bool IsUsableAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (trimmed is "-" or "::" or "0.0.0.0") return false;
        return IPAddress.TryParse(trimmed, out _);
    }

    private static string Normalize(string value)
    {
        var trimmed = value.Trim();
        if (!IPAddress.TryParse(trimmed, out var address)) return trimmed;

        // IIS logs IPv4-mapped IPv6 (::ffff:10.0.0.4) for some local traffic.
        // IPAddress.ToString() keeps the mapped form, which reads as noise in a
        // narrow grid column — unwrap it to the plain IPv4 the operator expects.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString();
    }
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
