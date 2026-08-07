using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.ui.Services;
using com.logdb.windows.collector.ui.ViewModels.Infrastructure;

namespace com.logdb.windows.collector.ui.ViewModels.Pages;

/// <summary>One entry in the Blocked IPs source picker. <see cref="Source"/>
/// empty + <see cref="Kind"/> "all" means everything; empty source with kind
/// "public" is the public-feed roll-up; a named source restricts to that feed.</summary>
public sealed record BlockedSourceOption(string Label, string Source, string Kind)
{
    public override string ToString() => Label;
}

/// <summary>
/// One row of the Blocked IPs drawer grid. <see cref="BlockedAt"/> is already
/// converted to local time and formatted; it carries a "~" prefix when the
/// service could only give a first-observed time rather than the real block
/// time, and <see cref="BlockedAtTooltip"/> spells that out in words.
/// <see cref="Reason"/> / <see cref="AddedBy"/> are populated for Guard-sourced
/// IPs only — public threat feeds ship no per-entry provenance at all.
/// </summary>
/// <param name="BlockedAtUtc">Sort key for the Blocked column. The displayed
/// string carries a "~" prefix for approximate times, which would sort every
/// approximate row into one clump ahead of the rest; sorting on the raw instant
/// keeps the column chronological regardless.</param>
public sealed record BlockedIpRow(
    string Ip,
    string BlockedAt,
    string BlockedAtTooltip,
    string Source,
    string Reason,
    string AddedBy,
    DateTime BlockedAtUtc);

/// <summary>
/// One line in the enforcement panel. <see cref="IsWarning"/> drives the
/// colour — warnings are states that will surprise the operator later
/// (third-party feeds enforcing with no whitelist, the operator's own
/// blocklist switched off), notices are merely informational.
/// </summary>
public sealed record FirewallNoticeRow(string Icon, string Text, bool IsWarning);

public sealed class DataSourceFirewallHistoryRow
{
    public DataSourceFirewallHistoryRow(Action<DataSourceFirewallHistoryRow> openDetails)
    {
        OpenDetailsCommand = new RelayCommand(() => openDetails(this));
    }

    public string TimeLocal { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;

    /// <summary>Full structured entry backing this row — null when the row was
    /// scraped from diagnostics (older collector fallback), in which case the
    /// detail drawer only has the display strings to show.</summary>
    public FirewallRuleHistoryEntryDto? Entry { get; init; }

    /// <summary>Per-row Details button — selects the row and opens the detail
    /// drawer, same as double-clicking it.</summary>
    public RelayCommand OpenDetailsCommand { get; }
}

public sealed class DataSourceFirewallRuleRow : ObservableObject
{
    private readonly Func<DataSourceFirewallRuleRow, Task> _deleteAsync;
    private string _deleteButtonText = "Delete";
    private bool _isConfirmingDelete;
    private CancellationTokenSource? _confirmDeleteCts;

    public DataSourceFirewallRuleRow(
        Func<DataSourceFirewallRuleRow, Task> deleteAsync,
        Func<DataSourceFirewallRuleRow, Task> viewIpsAsync)
    {
        _deleteAsync = deleteAsync;
        DeleteCommand = new AsyncRelayCommand(HandleDeleteClickAsync);
        ViewIpsCommand = new AsyncRelayCommand(() => viewIpsAsync(this));
    }

    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Direction { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int IpCount { get; init; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand ViewIpsCommand { get; }

    /// <summary>A LogDB rule on this host that the collector did not create —
    /// shown so the operator can see everything enforcing under the LogDB name,
    /// but not deletable from here: the collector doesn't own it and couldn't
    /// re-create it.</summary>
    public bool IsUnmanaged { get; init; }

    /// <summary>Drives the Delete button — false for unmanaged rules.</summary>
    public bool CanDelete => !IsUnmanaged;

    /// <summary>"Delete" normally; "Confirm?" for the 3-second confirmation
    /// window after the first click.</summary>
    public string DeleteButtonText
    {
        get => _deleteButtonText;
        private set => SetProperty(ref _deleteButtonText, value);
    }

    public bool IsConfirmingDelete
    {
        get => _isConfirmingDelete;
        private set => SetProperty(ref _isConfirmingDelete, value);
    }

    /// <summary>Two-step confirm: the first click arms the button ("Confirm?")
    /// and starts a 3-second revert timer; a second click within the window
    /// runs the actual delete.</summary>
    private async Task HandleDeleteClickAsync()
    {
        if (!IsConfirmingDelete)
        {
            IsConfirmingDelete = true;
            DeleteButtonText = "Confirm?";

            _confirmDeleteCts?.Cancel();
            _confirmDeleteCts = new CancellationTokenSource();
            var token = _confirmDeleteCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(3000, token);
                    if (!token.IsCancellationRequested)
                    {
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(ResetDeleteConfirmation);
                    }
                }
                catch (OperationCanceledException)
                {
                    // second click (or a newer arm) cancelled the revert
                }
            }, token);

            return;
        }

        _confirmDeleteCts?.Cancel();
        ResetDeleteConfirmation();
        await _deleteAsync(this);
    }

    private void ResetDeleteConfirmation()
    {
        IsConfirmingDelete = false;
        DeleteButtonText = "Delete";
    }
}

public sealed class FirewallPageViewModel : PageViewModelBase
{
    private readonly LocalCollectorAdminClient _adminClient;
    private readonly Action<string, bool> _statusCallback;

    private string _firewallTabSummary = "Firewall: not loaded.";
    private string _firewallTabRuntime = "Runtime: unavailable.";
    private string _firewallRulesSummary = "Active rules: not loaded.";
    private string _publicFeedsHeadline = string.Empty;
    private string _guardHeadline = string.Empty;
    private bool _publicFeedsOn;
    private bool _guardOn;

    /// <summary>True when the config carried no publicBlocklists section at all,
    /// so both this UI and the service fall back to the four stock feeds. The
    /// operator never chose them, which is worth saying out loud.</summary>
    private bool _feedsAreStockDefaults;

    /// <summary>LogDB rules on the host that this collector does not manage,
    /// from the last Active Rules refresh. Feeds the enforcement panel.</summary>
    private int _unmanagedRuleCount;
    private bool _firewallIpsPanelVisible;
    private string _firewallIpsTitle = string.Empty;
    private string _firewallIpsFilter = string.Empty;
    private string _firewallIpsCountText = string.Empty;
    private List<string> _firewallIpsAll = new();
    private bool _firewallDetailVisible;
    private string _firewallDetailTitle = string.Empty;
    private string _firewallDetailTime = string.Empty;
    private string _firewallDetailResult = string.Empty;
    private string _firewallDetailMeta = string.Empty;
    private string _firewallDetailMessage = string.Empty;
    private string _firewallDetailAddedHeader = string.Empty;
    private string _firewallDetailRemovedHeader = string.Empty;
    private DataSourceFirewallHistoryRow? _selectedFirewallHistoryRow;
    private DataSourceFirewallRuleRow? _selectedFirewallRuleRow;
    private bool _firewallBlockedVisible;
    private string _firewallBlockedFilter = string.Empty;
    private string _firewallBlockedCountText = string.Empty;
    private BlockedSourceOption? _selectedBlockedSource;
    private bool _suppressBlockedSourceReload;
    private CancellationTokenSource? _firewallBlockedQueryCts;
    private readonly SemaphoreSlim _firewallHistoryRefreshLock = new(1, 1);
    private readonly SemaphoreSlim _firewallRulesRefreshLock = new(1, 1);

    private bool _isAdministrator;
    private bool _firewallEnabled;
    private int _firewallPollIntervalSeconds = 900;
    private string _firewallRuleNamePrefix = "LogDB Firewall";
    private bool _firewallDryRun;
    private string _firewallWhitelistPath = string.Empty;
    private string _firewallBlocklistSummary = "No blocklists loaded.";
    private bool _firewallCustomEnabled;
    private string _firewallCustomDisplayName = "LogDB Guard";
    private string _firewallCustomGuardUrl = string.Empty;
    private BlocklistFeedRowViewModel? _selectedBlocklistFeed;
    private string _firewallRuntimeStatus = "Runtime: unavailable.";
    private string _firewallHint =
        "Firewall sync periodically fetches public IP-reputation feeds and applies them as inbound block rules.";

    public FirewallPageViewModel(LocalCollectorAdminClient adminClient, Action<string, bool> statusCallback)
        : base("Firewall")
    {
        _adminClient = adminClient;
        _statusCallback = statusCallback;

        FirewallHistoryRows = new ObservableCollection<DataSourceFirewallHistoryRow>();
        FirewallRuleRows = new ObservableCollection<DataSourceFirewallRuleRow>();
        FirewallIpsView = new ObservableCollection<string>();
        FirewallDetailAddedIps = new ObservableCollection<string>();
        FirewallDetailRemovedIps = new ObservableCollection<string>();
        FirewallBlockedRows = new ObservableCollection<BlockedIpRow>();
        BlocklistFeeds = new ObservableCollection<BlocklistFeedRowViewModel>();
        EnforcementNotices = new ObservableCollection<FirewallNoticeRow>();

        RefreshFirewallHistoryCommand = new AsyncRelayCommand(RefreshFirewallHistoryAsync);
        RefreshFirewallRulesCommand = new AsyncRelayCommand(RefreshFirewallRulesAsync);
        CloseFirewallIpsCommand = new RelayCommand(() => FirewallIpsPanelVisible = false);
        CloseFirewallDetailCommand = new RelayCommand(() => FirewallDetailVisible = false);
        OpenFirewallBlockedIpsCommand = new AsyncRelayCommand(OpenFirewallBlockedIpsAsync);
        CloseFirewallBlockedCommand = new RelayCommand(() => FirewallBlockedVisible = false);

        SaveFirewallConfigCommand = new AsyncRelayCommand(SaveFirewallConfigAsync);
        ApplyFirewallNowCommand = new AsyncRelayCommand(ApplyFirewallNowAsync);
        RemoveFirewallRulesCommand = new AsyncRelayCommand(RemoveFirewallRulesAsync);
        AddBlocklistFeedCommand = new RelayCommand(AddBlocklistFeed);
        RemoveSelectedBlocklistFeedCommand = new RelayCommand(RemoveSelectedBlocklistFeed, () => _selectedBlocklistFeed != null);
    }

    public ObservableCollection<DataSourceFirewallHistoryRow> FirewallHistoryRows { get; }
    public ObservableCollection<DataSourceFirewallRuleRow> FirewallRuleRows { get; }
    public ObservableCollection<string> FirewallIpsView { get; }
    public ObservableCollection<string> FirewallDetailAddedIps { get; }
    public ObservableCollection<string> FirewallDetailRemovedIps { get; }
    public ObservableCollection<BlockedIpRow> FirewallBlockedRows { get; }
    public ObservableCollection<BlocklistFeedRowViewModel> BlocklistFeeds { get; }

    /// <summary>Warnings and notices about what this collector is actually
    /// enforcing right now — see <see cref="RebuildEnforcementNotices"/>.</summary>
    public ObservableCollection<FirewallNoticeRow> EnforcementNotices { get; }

    public AsyncRelayCommand RefreshFirewallHistoryCommand { get; }
    public AsyncRelayCommand RefreshFirewallRulesCommand { get; }
    public RelayCommand CloseFirewallIpsCommand { get; }
    public RelayCommand CloseFirewallDetailCommand { get; }
    public AsyncRelayCommand OpenFirewallBlockedIpsCommand { get; }
    public RelayCommand CloseFirewallBlockedCommand { get; }

    public AsyncRelayCommand SaveFirewallConfigCommand { get; }
    public AsyncRelayCommand ApplyFirewallNowCommand { get; }
    public AsyncRelayCommand RemoveFirewallRulesCommand { get; }
    public RelayCommand AddBlocklistFeedCommand { get; }
    public RelayCommand RemoveSelectedBlocklistFeedCommand { get; }

    public string FirewallTabSummary
    {
        get => _firewallTabSummary;
        set => SetProperty(ref _firewallTabSummary, value);
    }

    public string FirewallTabRuntime
    {
        get => _firewallTabRuntime;
        set => SetProperty(ref _firewallTabRuntime, value);
    }

    public string FirewallRulesSummary
    {
        get => _firewallRulesSummary;
        set => SetProperty(ref _firewallRulesSummary, value);
    }

    /// <summary>"ON — 4 feed(s): …" / "OFF — …" for the third-party feeds.</summary>
    public string PublicFeedsHeadline
    {
        get => _publicFeedsHeadline;
        private set => SetProperty(ref _publicFeedsHeadline, value);
    }

    /// <summary>The same for the operator's own Guard blocklist. Kept as a
    /// separate line from the public feeds precisely because the two are
    /// independent and default in opposite directions.</summary>
    public string GuardHeadline
    {
        get => _guardHeadline;
        private set => SetProperty(ref _guardHeadline, value);
    }

    public bool PublicFeedsOn
    {
        get => _publicFeedsOn;
        private set => SetProperty(ref _publicFeedsOn, value);
    }

    public bool GuardOn
    {
        get => _guardOn;
        private set => SetProperty(ref _guardOn, value);
    }

    public bool FirewallIpsPanelVisible
    {
        get => _firewallIpsPanelVisible;
        set
        {
            if (SetProperty(ref _firewallIpsPanelVisible, value))
            {
                NotifyPropertyChanged(nameof(FirewallSidePanelVisible));
            }
        }
    }

    /// <summary>True when any right-hand drawer (history detail, rule IPs, or
    /// blocked-IP list) is open — drives the shared drawer column and splitter.</summary>
    public bool FirewallSidePanelVisible => _firewallDetailVisible || _firewallIpsPanelVisible || _firewallBlockedVisible;

    public bool FirewallBlockedVisible
    {
        get => _firewallBlockedVisible;
        set
        {
            if (SetProperty(ref _firewallBlockedVisible, value))
            {
                NotifyPropertyChanged(nameof(FirewallSidePanelVisible));
            }
        }
    }

    public string FirewallBlockedFilter
    {
        get => _firewallBlockedFilter;
        set
        {
            if (SetProperty(ref _firewallBlockedFilter, value))
            {
                ScheduleFirewallBlockedQuery();
            }
        }
    }

    public string FirewallBlockedCountText
    {
        get => _firewallBlockedCountText;
        set => SetProperty(ref _firewallBlockedCountText, value);
    }

    /// <summary>Source picker for the Blocked IPs drawer: all sources, the
    /// public-feed roll-up, the Guard subscription, then each feed.</summary>
    public ObservableCollection<BlockedSourceOption> BlockedSourceOptions { get; } = new();

    public BlockedSourceOption? SelectedBlockedSource
    {
        get => _selectedBlockedSource;
        set
        {
            if (SetProperty(ref _selectedBlockedSource, value) && !_suppressBlockedSourceReload)
            {
                _ = RefreshFirewallBlockedIpsAsync();
            }
        }
    }

    public string FirewallIpsTitle
    {
        get => _firewallIpsTitle;
        set => SetProperty(ref _firewallIpsTitle, value);
    }

    public string FirewallIpsFilter
    {
        get => _firewallIpsFilter;
        set
        {
            if (SetProperty(ref _firewallIpsFilter, value))
            {
                RefilterFirewallIps();
            }
        }
    }

    public string FirewallIpsCountText
    {
        get => _firewallIpsCountText;
        set => SetProperty(ref _firewallIpsCountText, value);
    }

    public bool FirewallDetailVisible
    {
        get => _firewallDetailVisible;
        set
        {
            if (SetProperty(ref _firewallDetailVisible, value))
            {
                NotifyPropertyChanged(nameof(FirewallSidePanelVisible));
            }
        }
    }

    public string FirewallDetailTitle
    {
        get => _firewallDetailTitle;
        set => SetProperty(ref _firewallDetailTitle, value);
    }

    public string FirewallDetailTime
    {
        get => _firewallDetailTime;
        set => SetProperty(ref _firewallDetailTime, value);
    }

    public string FirewallDetailResult
    {
        get => _firewallDetailResult;
        set => SetProperty(ref _firewallDetailResult, value);
    }

    public string FirewallDetailMeta
    {
        get => _firewallDetailMeta;
        set => SetProperty(ref _firewallDetailMeta, value);
    }

    public string FirewallDetailMessage
    {
        get => _firewallDetailMessage;
        set => SetProperty(ref _firewallDetailMessage, value);
    }

    public string FirewallDetailAddedHeader
    {
        get => _firewallDetailAddedHeader;
        set => SetProperty(ref _firewallDetailAddedHeader, value);
    }

    public string FirewallDetailRemovedHeader
    {
        get => _firewallDetailRemovedHeader;
        set => SetProperty(ref _firewallDetailRemovedHeader, value);
    }

    public DataSourceFirewallHistoryRow? SelectedFirewallHistoryRow
    {
        get => _selectedFirewallHistoryRow;
        set => SetProperty(ref _selectedFirewallHistoryRow, value);
    }

    public DataSourceFirewallRuleRow? SelectedFirewallRuleRow
    {
        get => _selectedFirewallRuleRow;
        set => SetProperty(ref _selectedFirewallRuleRow, value);
    }

    public bool IsAdministrator
    {
        get => _isAdministrator;
        private set
        {
            if (!SetProperty(ref _isAdministrator, value))
            {
                return;
            }

            NotifyPropertyChanged(nameof(CanSaveFirewallConfig));
            NotifyPropertyChanged(nameof(CanRemoveFirewallRules));
        }
    }

    public bool FirewallEnabled
    {
        get => _firewallEnabled;
        set
        {
            if (SetProperty(ref _firewallEnabled, value)) RebuildEnforcementNotices();
        }
    }

    public int FirewallPollIntervalSeconds
    {
        get => _firewallPollIntervalSeconds;
        set => SetProperty(ref _firewallPollIntervalSeconds, value);
    }

    public string FirewallRuleNamePrefix
    {
        get => _firewallRuleNamePrefix;
        set => SetProperty(ref _firewallRuleNamePrefix, value);
    }

    public bool FirewallDryRun
    {
        get => _firewallDryRun;
        set
        {
            if (SetProperty(ref _firewallDryRun, value)) RebuildEnforcementNotices();
        }
    }

    public string FirewallWhitelistPath
    {
        get => _firewallWhitelistPath;
        set
        {
            if (SetProperty(ref _firewallWhitelistPath, value)) RebuildEnforcementNotices();
        }
    }

    public string FirewallBlocklistSummary
    {
        get => _firewallBlocklistSummary;
        private set => SetProperty(ref _firewallBlocklistSummary, value);
    }

    public bool FirewallCustomEnabled
    {
        get => _firewallCustomEnabled;
        set
        {
            if (SetProperty(ref _firewallCustomEnabled, value)) RebuildEnforcementNotices();
        }
    }

    public string FirewallCustomDisplayName
    {
        get => _firewallCustomDisplayName;
        set => SetProperty(ref _firewallCustomDisplayName, value);
    }

    public string FirewallCustomGuardUrl
    {
        get => _firewallCustomGuardUrl;
        set => SetProperty(ref _firewallCustomGuardUrl, value);
    }

    public BlocklistFeedRowViewModel? SelectedBlocklistFeed
    {
        get => _selectedBlocklistFeed;
        set
        {
            if (SetProperty(ref _selectedBlocklistFeed, value))
                RemoveSelectedBlocklistFeedCommand.RaiseCanExecuteChanged();
        }
    }

    public string FirewallRuntimeStatus
    {
        get => _firewallRuntimeStatus;
        private set => SetProperty(ref _firewallRuntimeStatus, value);
    }

    public string FirewallHint
    {
        get => _firewallHint;
        private set => SetProperty(ref _firewallHint, value);
    }

    public bool CanSaveFirewallConfig => _adminClient.SelectedTarget != null;
    public bool CanRemoveFirewallRules => CanSaveFirewallConfig && IsAdministrator;

    public override async Task RefreshAsync()
    {
        IsAdministrator = ServiceControl.IsAdministrator();

        RefreshFirewallConfig();
        await RefreshFirewallSummaryAsync();
        await RefreshFirewallHistoryAsync();
        await RefreshFirewallRulesAsync();

        NotifyPropertyChanged(nameof(CanSaveFirewallConfig));
        NotifyPropertyChanged(nameof(CanRemoveFirewallRules));
    }

    /// <summary>Loads the firewall section of the working config into the
    /// editable configuration properties (moved from Service Management).</summary>
    private void RefreshFirewallConfig()
    {
        var config = _adminClient.SnapshotWorkingConfig();
        FirewallEnabled = config.Firewall.Enabled;
        FirewallPollIntervalSeconds = config.Firewall.PollIntervalSeconds;
        FirewallRuleNamePrefix = config.Firewall.RuleNamePrefix;
        FirewallDryRun = config.Firewall.DryRun;
        FirewallWhitelistPath = config.Firewall.WhitelistPath;
        FirewallCustomEnabled = config.Firewall.CustomBlocklist.Enabled;
        FirewallCustomDisplayName = string.IsNullOrWhiteSpace(config.Firewall.CustomBlocklist.DisplayName)
            ? "LogDB Guard"
            : config.Firewall.CustomBlocklist.DisplayName;
        FirewallCustomGuardUrl = config.Firewall.CustomBlocklist.GuardUrl;
        LoadBlocklistFeedsFromConfig(config.Firewall.PublicBlocklists);
        var enabledFeedCount = BlocklistFeeds.Count(row => row.Enabled);
        FirewallBlocklistSummary = enabledFeedCount == 0
            ? (FirewallCustomEnabled ? "No public blocklists enabled (Guard only)." : "No blocklists enabled.")
            : $"{enabledFeedCount} public blocklist(s) enabled"
              + (FirewallCustomEnabled ? " + LogDB Guard." : ".");

        FirewallHint = FirewallEnabled
            ? "Firewall sync is active. Blocked IPs from LogDB will be applied as inbound block rules."
            : "Enable firewall sync to automatically block malicious IPs detected by LogDB Guard.";

        RebuildEnforcementNotices();
    }

    /// <summary>
    /// Rebuilds the "what is actually being enforced" panel.
    ///
    /// This exists because the two blocklist sources are independent and their
    /// defaults point opposite ways: the four public threat feeds ship enabled,
    /// the operator's own Guard subscription ships disabled. Someone who blocks
    /// an IP in the desktop app and then enables "firewall sync" gets tens of
    /// thousands of third-party IPs blocked and none of their own — and the old
    /// UI stated only a combined feed count, so nothing said so.
    /// </summary>
    private void RebuildEnforcementNotices()
    {
        var enabledFeeds = BlocklistFeeds.Where(f => f.Enabled).ToList();
        PublicFeedsOn = enabledFeeds.Count > 0;
        GuardOn = FirewallCustomEnabled;

        var feedNames = string.Join(", ", enabledFeeds
            .Select(f => string.IsNullOrWhiteSpace(f.DisplayName) ? f.FeedId : f.DisplayName));

        PublicFeedsHeadline = PublicFeedsOn
            ? $"ON — {enabledFeeds.Count} third-party feed(s): {feedNames}"
            : "OFF — no public threat feeds enabled.";

        GuardHeadline = GuardOn
            ? $"ON — subscribed to '{FirewallCustomDisplayName}'."
            : "OFF — IPs you block in the LogDB desktop app are NOT applied by this collector.";

        EnforcementNotices.Clear();

        if (!FirewallEnabled)
        {
            EnforcementNotices.Add(new FirewallNoticeRow("○",
                "Firewall sync is disabled — this collector is not applying any block rules. "
                + "Everything below describes what would be applied once you enable it.", false));
            return;
        }

        // The headline asymmetry, stated plainly.
        if (PublicFeedsOn && !GuardOn)
        {
            EnforcementNotices.Add(new FirewallNoticeRow("⚠",
                "This collector is enforcing third-party threat feeds but none of your own blocks. "
                + "The Guard subscription is off, so IPs you block in the LogDB desktop app never reach this host. "
                + "Enable 'Guard subscription' in Configuration if that is what you expected it to do.", true));
        }

        // Lockout risk. Worth a warning on its own: it is silent until it isn't.
        if (PublicFeedsOn && string.IsNullOrWhiteSpace(FirewallWhitelistPath))
        {
            EnforcementNotices.Add(new FirewallNoticeRow("⚠",
                "No whitelist file is set. Public feeds are reputation-scored and do list cloud-provider "
                + "addresses — if one covers your RDP, VPN or monitoring source, this host will block you out "
                + "and the rule will be re-applied every sync. Set 'Whitelist Path' before relying on public feeds.", true));
        }

        if (_feedsAreStockDefaults)
        {
            EnforcementNotices.Add(new FirewallNoticeRow("⚠",
                "Your saved config contains no public-feed section, so the collector falls back to its four stock "
                + "feeds (FireHOL Level 1 & 2, Tor exits, IPsum ≥ 3). These are shown below but were never chosen by "
                + "you — click 'Apply Firewall Config' to write them in explicitly, or disable the ones you don't want.", true));
        }

        if (FirewallDryRun)
        {
            EnforcementNotices.Add(new FirewallNoticeRow("○",
                "Dry run is on — rules are logged but never applied, and the Blocked IPs list stays empty.", false));
        }

        if (_unmanagedRuleCount > 0)
        {
            EnforcementNotices.Add(new FirewallNoticeRow("○",
                $"{_unmanagedRuleCount} other LogDB rule(s) are blocking on this host but are not managed here — "
                + "they come from the desktop app's firewall-export script and only change when it is re-run. "
                + "They are listed under Active Rules as 'not managed here'.", false));
        }

        if (EnforcementNotices.Count == 0)
        {
            EnforcementNotices.Add(new FirewallNoticeRow("✓",
                "Both sources are configured and a whitelist is set.", false));
        }
    }

    private async Task OpenFirewallBlockedIpsAsync()
    {
        FirewallDetailVisible = false;      // the drawers share one column
        FirewallIpsPanelVisible = false;
        FirewallBlockedVisible = true;
        await RefreshFirewallBlockedIpsAsync();
    }

    /// <summary>Filter keystrokes re-query the service; debounced so a fast
    /// typist causes one pipe round-trip, not one per character.</summary>
    private void ScheduleFirewallBlockedQuery()
    {
        if (!FirewallBlockedVisible)
        {
            return;
        }

        _firewallBlockedQueryCts?.Cancel();
        _firewallBlockedQueryCts = new CancellationTokenSource();
        var token = _firewallBlockedQueryCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, token);
                if (!token.IsCancellationRequested)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RefreshFirewallBlockedIpsAsync);
                }
            }
            catch (OperationCanceledException)
            {
                // debounced
            }
        }, token);
    }

    private async Task RefreshFirewallBlockedIpsAsync()
    {
        try
        {
            var selected = SelectedBlockedSource;
            var result = await _adminClient.GetFirewallBlockedIpsAsync(
                FirewallBlockedFilter,
                500,
                selected?.Source,
                selected?.Kind);

            FirewallBlockedRows.Clear();

            if (result == null)
            {
                FirewallBlockedCountText = "Not available — the running collector predates the blocked-IP index (update the service).";
                return;
            }

            RebuildBlockedSourceOptions(result);

            foreach (var entry in result.Entries)
            {
                var local = entry.BlockedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                FirewallBlockedRows.Add(new BlockedIpRow(
                    entry.Ip,
                    entry.BlockedAtApproximate ? "~" + local : local,
                    entry.BlockedAtApproximate
                        ? $"Approximate: {local} is when this collector first saw the IP in a rule, not when it "
                          + "was blocked. Public threat feeds carry no per-entry dates, and IPs already applied "
                          + "before the index existed have no earlier record."
                        : $"{local} — the block time reported by the LogDB Guard backend.",
                    entry.Source,
                    entry.Reason,
                    entry.AddedBy,
                    entry.BlockedAtUtc));
            }

            FirewallBlockedCountText = (result.Matched > result.Entries.Count
                ? $"showing first {result.Entries.Count} of {result.Matched} matched ({result.Total} blocked in total) — refine the filter"
                : $"{result.Matched} shown · {result.Total} blocked in total")
                + DescribeMissingGuardSource(result);
        }
        catch (Exception ex)
        {
            _statusCallback($"Blocked IPs refresh failed: {ex.Message}", false);
        }
    }

    /// <summary>
    /// Explains an absent Guard section rather than leaving the operator to read
    /// an empty list as "the IP I blocked didn't sync". The index only ever
    /// contains what this collector applied, so a Guard subscription that is
    /// switched off produces exactly the same empty view as one that is broken —
    /// and the difference is the whole answer.
    /// </summary>
    private string DescribeMissingGuardSource(BlockedIpListResponseDto result)
    {
        if (result.Sources.Any(s => s.IsGuard)) return string.Empty;

        return FirewallCustomEnabled
            ? $"  ⚠ No IPs from '{FirewallCustomDisplayName}' are indexed yet — the subscription is enabled but has not "
              + "completed a sync. Check the module status and the collector log for a Guard fetch error."
            : "  ⚠ The LogDB Guard subscription is off, so IPs you block in the desktop app are not applied by this "
              + "collector and never appear here. Enable it above under 'LogDB Guard (custom blocklist)'.";
    }

    /// <summary>
    /// Rebuilds the source picker from the index's own source list: the two
    /// roll-ups (all public feeds / Guard) then one entry per feed with its IP
    /// count. Options are only rebuilt when the set actually changed, so the
    /// combo doesn't flicker or lose the selection on every keystroke.
    /// </summary>
    private void RebuildBlockedSourceOptions(BlockedIpListResponseDto result)
    {
        var options = new List<BlockedSourceOption>
        {
            new("All sources", string.Empty, BlockedIpKinds.All)
        };

        var publicSources = result.Sources.Where(s => !s.IsGuard).ToList();
        var guardSources = result.Sources.Where(s => s.IsGuard).ToList();

        if (publicSources.Count > 0)
        {
            var publicTotal = publicSources.Sum(s => s.Count);
            options.Add(new($"All public feeds ({publicTotal:N0})", string.Empty, BlockedIpKinds.Public));
        }

        foreach (var guard in guardSources)
        {
            options.Add(new($"{guard.Source} — custom ({guard.Count:N0})", guard.Source, BlockedIpKinds.Guard));
        }

        foreach (var feed in publicSources)
        {
            options.Add(new($"{feed.Source} ({feed.Count:N0})", feed.Source, BlockedIpKinds.All));
        }

        if (options.Select(o => o.Label).SequenceEqual(BlockedSourceOptions.Select(o => o.Label)))
        {
            return;
        }

        var previous = SelectedBlockedSource;
        _suppressBlockedSourceReload = true;
        try
        {
            BlockedSourceOptions.Clear();
            foreach (var option in options) BlockedSourceOptions.Add(option);

            SelectedBlockedSource =
                BlockedSourceOptions.FirstOrDefault(o => previous != null
                    && o.Source == previous.Source && o.Kind == previous.Kind)
                ?? BlockedSourceOptions[0];
        }
        finally
        {
            _suppressBlockedSourceReload = false;
        }
    }

    /// <summary>Double-click entry point on the Active Rules grid — same as the
    /// row's IPs button.</summary>
    public async Task OpenSelectedFirewallRuleIpsAsync()
    {
        var row = SelectedFirewallRuleRow;
        if (row != null)
        {
            await ViewFirewallRuleIpsAsync(row);
        }
    }

    /// <summary>Fills and shows the detail drawer for the selected history row
    /// (invoked by the view on double-click or the row's Details button).</summary>
    public void OpenFirewallHistoryDetail()
    {
        var row = SelectedFirewallHistoryRow;
        if (row == null)
        {
            return;
        }

        FirewallDetailAddedIps.Clear();
        FirewallDetailRemovedIps.Clear();

        if (row.Entry is { } entry)
        {
            FirewallDetailTitle = $"{DescribeFirewallHistoryAction(entry.Action)}" +
                                  (string.IsNullOrWhiteSpace(entry.RuleName) ? "" : $" — {entry.RuleName}");
            FirewallDetailTime = $"{entry.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} local · {entry.TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC";
            FirewallDetailResult = entry.Success ? (entry.DryRun ? "Dry run — firewall untouched" : "OK") : "Error";
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(entry.Source)) meta.Add($"source: {entry.Source}");
            if (entry.IpCount > 0) meta.Add($"{entry.IpCount} IPs in rule");
            FirewallDetailMeta = string.Join(" · ", meta);
            FirewallDetailMessage = entry.Message;

            FirewallDetailAddedHeader = BuildDeltaHeader("Added", entry.AddedCount, entry.AddedIps.Count);
            FirewallDetailRemovedHeader = BuildDeltaHeader("Removed", entry.RemovedCount, entry.RemovedIps.Count);
            foreach (var ip in entry.AddedIps) FirewallDetailAddedIps.Add(ip);
            foreach (var ip in entry.RemovedIps) FirewallDetailRemovedIps.Add(ip);
        }
        else
        {
            // Diagnostics-scraped row (older collector) — display strings only.
            FirewallDetailTitle = row.Action;
            FirewallDetailTime = $"{row.TimeLocal} local";
            FirewallDetailResult = row.Result;
            FirewallDetailMeta = string.Empty;
            FirewallDetailMessage = row.Details;
            FirewallDetailAddedHeader = string.Empty;
            FirewallDetailRemovedHeader = string.Empty;
        }

        FirewallIpsPanelVisible = false;   // the drawers share one column
        FirewallBlockedVisible = false;
        FirewallDetailVisible = true;
    }

    private static string BuildDeltaHeader(string label, int totalCount, int sampleCount)
    {
        if (totalCount <= 0) return string.Empty;
        return totalCount > sampleCount
            ? $"{label} — {totalCount} (sample of first {sampleCount})"
            : $"{label} — {totalCount}";
    }

    /// <summary>Plain-text rendering of the open detail drawer for the Copy button.</summary>
    public string BuildFirewallDetailClipboardText()
    {
        var lines = new List<string>
        {
            FirewallDetailTitle,
            FirewallDetailTime,
            $"Result: {FirewallDetailResult}"
        };
        if (!string.IsNullOrWhiteSpace(FirewallDetailMeta)) lines.Add(FirewallDetailMeta);
        if (!string.IsNullOrWhiteSpace(FirewallDetailMessage)) lines.Add($"Message: {FirewallDetailMessage}");
        if (!string.IsNullOrWhiteSpace(FirewallDetailAddedHeader))
        {
            lines.Add(string.Empty);
            lines.Add(FirewallDetailAddedHeader);
            lines.AddRange(FirewallDetailAddedIps);
        }
        if (!string.IsNullOrWhiteSpace(FirewallDetailRemovedHeader))
        {
            lines.Add(string.Empty);
            lines.Add(FirewallDetailRemovedHeader);
            lines.AddRange(FirewallDetailRemovedIps);
        }
        return string.Join(Environment.NewLine, lines);
    }

    private async Task RefreshFirewallSummaryAsync()
    {
        var firewall = _adminClient.SnapshotWorkingConfig().Firewall;
        FirewallTabSummary = firewall.Enabled
            ? $"Firewall sync enabled (every {firewall.PollIntervalSeconds}s, prefix: {firewall.RuleNamePrefix})"
            : "Firewall sync disabled";

        var status = await _adminClient.GetStatusAsync();
        var firewallModule = status?.Modules
            .FirstOrDefault(module => module.Name.Equals("Firewall", StringComparison.OrdinalIgnoreCase));
        if (firewallModule == null)
        {
            FirewallTabRuntime = "Runtime: unavailable.";
            FirewallRuntimeStatus = "Runtime: unavailable.";
            return;
        }

        var runtime = string.IsNullOrWhiteSpace(firewallModule.LastError)
            ? $"Runtime: {firewallModule.State}"
            : $"Runtime: {firewallModule.State} ({firewallModule.LastError})";
        FirewallTabRuntime = runtime;
        FirewallRuntimeStatus = runtime;
    }

    private async Task RefreshFirewallHistoryAsync()
    {
        if (_adminClient.SelectedTarget == null)
        {
            FirewallHistoryRows.Clear();
            await RefreshFirewallSummaryAsync();
            return;
        }

        if (!await _firewallHistoryRefreshLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            var structured = await _adminClient.GetFirewallHistoryAsync(200);
            if (structured != null)
            {
                RebuildFirewallHistory(structured);
            }
            else
            {
                // Service predates the firewall-history command — fall back to
                // scraping the diagnostics ring like the UI always used to.
                var diagnostics = (await _adminClient.GetDiagnosticsAsync(500))
                    .OrderByDescending(entry => entry.TimestampUtc)
                    .ToList();
                RebuildFirewallHistoryFromDiagnostics(diagnostics);
            }

            await RefreshFirewallSummaryAsync();
        }
        catch (Exception ex)
        {
            _statusCallback($"Firewall history refresh failed: {ex.Message}", false);
        }
        finally
        {
            _firewallHistoryRefreshLock.Release();
        }
    }

    private async Task RefreshFirewallRulesAsync()
    {
        if (_adminClient.SelectedTarget == null)
        {
            FirewallRuleRows.Clear();
            FirewallRulesSummary = "Active rules: no collector instance selected.";
            return;
        }

        if (!await _firewallRulesRefreshLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            var (rules, error, unsupported) = await _adminClient.GetFirewallRulesAsync();
            FirewallRuleRows.Clear();

            if (rules == null)
            {
                FirewallRulesSummary = unsupported
                    ? "Active rules: the running collector does not support rule listing (update the service)."
                    : $"Active rules: refresh failed — {error}";
                return;
            }

            // Managed first, then the foreign LogDB rules — the operator's own
            // rules are the ones they act on; the rest is context.
            foreach (var rule in rules
                         .OrderBy(r => r.Unmanaged)
                         .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                FirewallRuleRows.Add(new DataSourceFirewallRuleRow(DeleteFirewallRuleAsync, ViewFirewallRuleIpsAsync)
                {
                    Id = rule.Id,
                    DisplayName = rule.DisplayName,
                    Source = rule.Source,
                    Direction = rule.Direction,
                    IpCount = rule.IpCount,
                    IsUnmanaged = rule.Unmanaged,
                    Status = rule.Unmanaged
                        ? (rule.Enabled ? "Enabled" : "Disabled") + " · not managed here"
                        : (rule.Enabled ? "Enabled" : "Disabled") + (rule.Legacy ? " (legacy)" : "")
                });
            }

            var managed = rules.Where(r => !r.Unmanaged).ToList();
            var unmanaged = rules.Where(r => r.Unmanaged).ToList();
            var totalIps = managed.Sum(r => r.IpCount);

            if (_unmanagedRuleCount != unmanaged.Count)
            {
                _unmanagedRuleCount = unmanaged.Count;
                RebuildEnforcementNotices();
            }

            var summary = managed.Count == 0
                ? "Active rules: none applied by this collector."
                : $"Active rules: {managed.Count} rule(s) blocking {totalIps:N0} IPs/CIDRs, read live from the OS firewall.";

            // Spelling this out is the whole point: an operator who blocked an IP
            // in the desktop app and then went looking for it here needs to know
            // these rules exist and that the collector neither wrote nor refreshes
            // them, rather than concluding sync is broken.
            if (unmanaged.Count > 0)
            {
                summary += $"  ⚠ {unmanaged.Count} other LogDB rule(s) on this host "
                           + $"({string.Join(", ", unmanaged.Take(3).Select(r => r.DisplayName))}"
                           + (unmanaged.Count > 3 ? ", …" : "")
                           + $") block {unmanaged.Sum(r => r.IpCount):N0} more IPs but are not managed by this collector — "
                           + "they come from the desktop app's firewall export and only change when that script is re-run.";
            }

            FirewallRulesSummary = summary;
        }
        catch (Exception ex)
        {
            _statusCallback($"Firewall rules refresh failed: {ex.Message}", false);
        }
        finally
        {
            _firewallRulesRefreshLock.Release();
        }
    }

    private async Task ViewFirewallRuleIpsAsync(DataSourceFirewallRuleRow row)
    {
        try
        {
            var (success, message, rule) = await _adminClient.GetFirewallRuleIpsAsync(row.Id);
            if (!success || rule == null)
            {
                _statusCallback(message, false);
                return;
            }

            _firewallIpsAll = rule.Ips;
            FirewallIpsTitle = $"{rule.DisplayName} — {rule.Ips.Count} IPs/CIDRs";
            FirewallIpsFilter = string.Empty;
            RefilterFirewallIps();
            FirewallDetailVisible = false;   // the drawers share one column
            FirewallBlockedVisible = false;
            FirewallIpsPanelVisible = true;
        }
        catch (Exception ex)
        {
            _statusCallback($"Failed to load IPs for '{row.DisplayName}': {ex.Message}", false);
        }
    }

    private void RefilterFirewallIps()
    {
        // Cap the rendered list: a 5000-row ListBox is pointless to scroll and
        // slow to build — the filter box is the way to find a specific address.
        const int maxShown = 500;
        var filter = _firewallIpsFilter.Trim();
        var matches = string.IsNullOrEmpty(filter)
            ? _firewallIpsAll
            : _firewallIpsAll.Where(ip => ip.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        FirewallIpsView.Clear();
        foreach (var ip in matches.Take(maxShown))
        {
            FirewallIpsView.Add(ip);
        }

        FirewallIpsCountText = matches.Count <= maxShown
            ? $"{matches.Count} shown"
            : $"showing first {maxShown} of {matches.Count} — refine the filter";
    }

    private async Task DeleteFirewallRuleAsync(DataSourceFirewallRuleRow row)
    {
        try
        {
            var (success, message) = await _adminClient.DeleteFirewallRuleAsync(row.Id, removeFromBackend: true);
            _statusCallback(message, success);

            if (success)
            {
                await RefreshFirewallRulesAsync();
                await RefreshFirewallHistoryAsync();
            }
        }
        catch (Exception ex)
        {
            _statusCallback($"Delete failed for '{row.DisplayName}': {ex.Message}", false);
        }
    }

    private void RebuildFirewallHistory(IReadOnlyList<FirewallRuleHistoryEntryDto> entries)
    {
        FirewallHistoryRows.Clear();
        foreach (var entry in entries)
        {
            FirewallHistoryRows.Add(new DataSourceFirewallHistoryRow(OpenHistoryRowDetail)
            {
                TimeLocal = entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                Action = DescribeFirewallHistoryAction(entry.Action),
                Result = entry.Success ? (entry.DryRun ? "Dry run" : "OK") : "Error",
                Details = DescribeFirewallHistoryDetails(entry),
                Entry = entry
            });
        }
    }

    /// <summary>Callback for a history row's Details button: select the row,
    /// then open the same drawer double-click uses.</summary>
    private void OpenHistoryRowDetail(DataSourceFirewallHistoryRow row)
    {
        SelectedFirewallHistoryRow = row;
        OpenFirewallHistoryDetail();
    }

    private static string DescribeFirewallHistoryAction(string action) => action switch
    {
        FirewallHistoryActions.RuleCreated => "Rule created",
        FirewallHistoryActions.RuleUpdated => "Rule updated",
        FirewallHistoryActions.RuleRemoved => "Rule removed",
        FirewallHistoryActions.SyncCompleted => "Sync",
        FirewallHistoryActions.SyncFailed => "Sync failed",
        FirewallHistoryActions.RemoveAll => "Remove all",
        _ => action
    };

    private static string DescribeFirewallHistoryDetails(FirewallRuleHistoryEntryDto entry)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.RuleName))
        {
            parts.Add(entry.IpCount > 0 ? $"{entry.RuleName} ({entry.IpCount} IPs)" : entry.RuleName);
        }

        if (!string.IsNullOrWhiteSpace(entry.Source) &&
            !string.Equals(entry.Source, entry.RuleName, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"source: {entry.Source}");
        }

        if (entry.AddedCount > 0)
        {
            parts.Add(DescribeIpDelta("+", entry.AddedCount, entry.AddedIps));
        }

        if (entry.RemovedCount > 0)
        {
            parts.Add(DescribeIpDelta("−", entry.RemovedCount, entry.RemovedIps));
        }

        if (!string.IsNullOrWhiteSpace(entry.Message))
        {
            parts.Add(entry.Message);
        }

        return SummarizeFirewallDetails(string.Join(" — ", parts));
    }

    /// <summary>"+3: 1.2.3.4, 5.6.7.8, 9.9.9.9" or "+120: 1.2.3.4, … (+115 more)".
    /// Entries written by pre-delta collector builds have counts of 0 and render
    /// no delta segment at all.</summary>
    private static string DescribeIpDelta(string sign, int totalCount, IReadOnlyList<string> sample)
    {
        const int shown = 5;
        var head = string.Join(", ", sample.Take(shown));
        var rest = totalCount - Math.Min(shown, sample.Count);
        return rest > 0
            ? $"{sign}{totalCount}: {head}, … (+{rest} more)"
            : $"{sign}{totalCount}: {head}";
    }

    private void RebuildFirewallHistoryFromDiagnostics(IReadOnlyList<DiagnosticEntryDto> diagnostics)
    {
        FirewallHistoryRows.Clear();
        var firewallEntries = diagnostics
            .Where(entry =>
                entry.Category.Contains("Firewall", StringComparison.OrdinalIgnoreCase) ||
                entry.Message.Contains("firewall", StringComparison.OrdinalIgnoreCase) ||
                entry.Message.Contains("New-NetFirewallRule", StringComparison.OrdinalIgnoreCase) ||
                entry.Message.Contains("Remove-NetFirewallRule", StringComparison.OrdinalIgnoreCase))
            .Take(120)
            .ToList();

        foreach (var entry in firewallEntries)
        {
            FirewallHistoryRows.Add(new DataSourceFirewallHistoryRow(OpenHistoryRowDetail)
            {
                TimeLocal = entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                Action = ClassifyFirewallAction(entry.Message),
                Result = ClassifyFirewallResult(entry),
                Details = SummarizeFirewallDetails(entry.Message)
            });
        }
    }

    private static string ClassifyFirewallAction(string message)
    {
        if (message.Contains("apply", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Applied firewall", StringComparison.OrdinalIgnoreCase))
        {
            return "Apply";
        }

        if (message.Contains("remove", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Removed firewall", StringComparison.OrdinalIgnoreCase))
        {
            return "Remove";
        }

        if (message.Contains("block", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("dropped", StringComparison.OrdinalIgnoreCase))
        {
            return "Block";
        }

        if (message.Contains("disabled", StringComparison.OrdinalIgnoreCase))
        {
            return "Disable";
        }

        if (message.Contains("elevation", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("administrator", StringComparison.OrdinalIgnoreCase))
        {
            return "Privilege";
        }

        if (message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("exception", StringComparison.OrdinalIgnoreCase))
        {
            return "Error";
        }

        return "Status";
    }

    private static string ClassifyFirewallResult(DiagnosticEntryDto entry)
    {
        if (entry.Level.Equals("Error", StringComparison.OrdinalIgnoreCase)
            || entry.Level.Equals("Critical", StringComparison.OrdinalIgnoreCase)
            || entry.Message.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || entry.Message.Contains("exception", StringComparison.OrdinalIgnoreCase))
        {
            return "Error";
        }

        if (entry.Message.Contains("elevation", StringComparison.OrdinalIgnoreCase)
            || entry.Message.Contains("administrator", StringComparison.OrdinalIgnoreCase))
        {
            return "Needs admin";
        }

        return "Info";
    }

    private static string SummarizeFirewallDetails(string message)
    {
        // 400, not 220: delta entries carry IP samples and got clipped at the old cap.
        var compact = Regex.Replace(message, @"\s+", " ").Trim();
        if (compact.Length <= 400)
        {
            return compact;
        }

        return compact[..400] + "...";
    }

    private async Task SaveFirewallConfigAsync()
    {
        if (_adminClient.SelectedTarget == null)
        {
            _statusCallback("No local collector target selected.", false);
            return;
        }

        var config = _adminClient.SnapshotWorkingConfig();
        config.Firewall.Enabled = FirewallEnabled;
        config.Firewall.PollIntervalSeconds = Math.Max(10, FirewallPollIntervalSeconds);
        config.Firewall.RuleNamePrefix = string.IsNullOrWhiteSpace(FirewallRuleNamePrefix)
            ? "LogDB Firewall"
            : FirewallRuleNamePrefix.Trim();
        config.Firewall.DryRun = FirewallDryRun;
        config.Firewall.WhitelistPath = FirewallWhitelistPath?.Trim() ?? string.Empty;
        config.Firewall.CustomBlocklist.Enabled = FirewallCustomEnabled;
        config.Firewall.CustomBlocklist.DisplayName = string.IsNullOrWhiteSpace(FirewallCustomDisplayName)
            ? "LogDB Guard"
            : FirewallCustomDisplayName.Trim();
        config.Firewall.CustomBlocklist.GuardUrl = FirewallCustomGuardUrl?.Trim() ?? string.Empty;
        config.Firewall.PublicBlocklists = WriteBlocklistFeedsToConfig();

        var result = await _adminClient.ApplyConfigAsync(config);
        _statusCallback(result.Success ? "Firewall configuration saved." : result.Message, result.Success);
        await RefreshAsync();
    }

    private async Task ApplyFirewallNowAsync()
    {
        if (!EnsureAdmin("Applying firewall rules"))
        {
            return;
        }

        var apply = await _adminClient.ApplyFirewallAsync();
        _statusCallback(apply.Message, apply.Success);
        await RefreshAsync();
    }

    private async Task RemoveFirewallRulesAsync()
    {
        if (!EnsureAdmin("Removing firewall rules"))
        {
            return;
        }

        var remove = await _adminClient.RemoveFirewallAsync();
        _statusCallback(remove.Message, remove.Success);
        await RefreshAsync();
    }

    private void BlocklistFeedRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BlocklistFeedRowViewModel.Enabled)
            or nameof(BlocklistFeedRowViewModel.DisplayName))
        {
            RebuildEnforcementNotices();
        }
    }

    private void LoadBlocklistFeedsFromConfig(Dictionary<string, PublicBlocklistFeedDto> source)
    {
        // Empty = feeds never configured; show the stock defaults the service
        // will actually sync with (see FirewallDefaults), so what the operator
        // sees matches what runs — and saving persists them into the config.
        // The flag is what lets the enforcement panel say these were never
        // actually chosen, rather than showing them as if they had been.
        _feedsAreStockDefaults = source.Count == 0;
        if (_feedsAreStockDefaults)
        {
            source = FirewallDefaults.CreatePublicBlocklists();
        }

        foreach (var existing in BlocklistFeeds)
        {
            existing.PropertyChanged -= BlocklistFeedRowChanged;
        }

        BlocklistFeeds.Clear();
        foreach (var (feedId, feed) in source.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            var row = new BlocklistFeedRowViewModel
            {
                FeedId = feedId,
                DisplayName = feed.DisplayName,
                Url = feed.Url,
                Enabled = feed.Enabled,
                MinScore = feed.MinScore
            };
            // Toggling a feed's On box has to move the headline immediately —
            // the panel is only trustworthy if it tracks the grid.
            row.PropertyChanged += BlocklistFeedRowChanged;
            BlocklistFeeds.Add(row);
        }
        SelectedBlocklistFeed = null;
        RebuildEnforcementNotices();
    }

    private Dictionary<string, PublicBlocklistFeedDto> WriteBlocklistFeedsToConfig()
    {
        // Last-write-wins on duplicate IDs (typical when a row is edited mid-rename).
        // Skip rows with no ID or no URL — those are half-typed entries we don't want
        // to round-trip through the wire format.
        var result = new Dictionary<string, PublicBlocklistFeedDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in BlocklistFeeds)
        {
            var id = (row.FeedId ?? string.Empty).Trim();
            var url = (row.Url ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(url)) continue;
            result[id] = new PublicBlocklistFeedDto
            {
                Enabled = row.Enabled,
                DisplayName = (row.DisplayName ?? string.Empty).Trim(),
                Url = url,
                MinScore = Math.Max(0, row.MinScore)
            };
        }
        return result;
    }

    private void AddBlocklistFeed()
    {
        var row = new BlocklistFeedRowViewModel
        {
            FeedId = $"custom_feed_{BlocklistFeeds.Count + 1}",
            DisplayName = "New feed",
            Url = string.Empty,
            Enabled = false,
            MinScore = 0
        };
        row.PropertyChanged += BlocklistFeedRowChanged;
        BlocklistFeeds.Add(row);
        SelectedBlocklistFeed = row;
        RebuildEnforcementNotices();
    }

    private void RemoveSelectedBlocklistFeed()
    {
        if (_selectedBlocklistFeed == null) return;
        _selectedBlocklistFeed.PropertyChanged -= BlocklistFeedRowChanged;
        BlocklistFeeds.Remove(_selectedBlocklistFeed);
        SelectedBlocklistFeed = null;
        RebuildEnforcementNotices();
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
