using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.ui.Services;
using com.logdb.windows.collector.ui.ViewModels.Infrastructure;

namespace com.logdb.windows.collector.ui.ViewModels.Pages;

public sealed class StringItemViewModel : ObservableObject
{
    private string _value = string.Empty;

    public StringItemViewModel()
    {
    }

    public StringItemViewModel(string value)
    {
        _value = value;
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}

public sealed class IisFilterRuleItemViewModel : ObservableObject
{
    private string _field = IisFilterFields.PathPrefix;
    private string _value = string.Empty;
    private bool _enabled = true;

    public IisFilterRuleItemViewModel()
    {
    }

    public IisFilterRuleItemViewModel(string field, string value, bool enabled = true)
    {
        _field = field;
        _value = value;
        _enabled = enabled;
    }

    public string Field
    {
        get => _field;
        set
        {
            if (SetProperty(ref _field, value))
            {
                NotifyPropertyChanged(nameof(FieldDisplay));
            }
        }
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string FieldDisplay => IisFilterFieldOption.GetDisplay(_field);
}

public sealed record IisFilterFieldOption(string Field, string Display)
{
    public override string ToString() => Display;

    private static readonly Dictionary<string, string> DisplayByField = new(StringComparer.OrdinalIgnoreCase)
    {
        [IisFilterFields.PathPrefix] = "URL path starts with",
        [IisFilterFields.Extension] = "File extension",
        [IisFilterFields.StatusCode] = "Status code",
        [IisFilterFields.Method] = "HTTP method",
        [IisFilterFields.UserAgentContains] = "User-Agent contains",
        [IisFilterFields.ClientIp] = "Client IP",
        [IisFilterFields.ClientIpPrefix] = "Client IP starts with",
        [IisFilterFields.MinTimeMs] = "Drop requests faster than (ms)",
        [IisFilterFields.MaxTimeMs] = "Drop requests slower than (ms)"
    };

    public static string GetDisplay(string field) =>
        DisplayByField.TryGetValue(field, out var display) ? display : field;

    public static IReadOnlyList<IisFilterFieldOption> All { get; } = DisplayByField
        .Select(kvp => new IisFilterFieldOption(kvp.Key, kvp.Value))
        .ToList();
}

public sealed class EventLogFilterRuleItemViewModel : ObservableObject
{
    private string _field = EventLogFilterFields.EventId;
    private string _value = string.Empty;
    private bool _enabled = true;

    public EventLogFilterRuleItemViewModel()
    {
    }

    public EventLogFilterRuleItemViewModel(string field, string value, bool enabled = true)
    {
        _field = field;
        _value = value;
        _enabled = enabled;
    }

    public string Field
    {
        get => _field;
        set
        {
            if (SetProperty(ref _field, value))
            {
                NotifyPropertyChanged(nameof(FieldDisplay));
            }
        }
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string FieldDisplay => EventLogFilterFieldOption.GetDisplay(_field);
}

public sealed record EventLogFilterFieldOption(string Field, string Display)
{
    public override string ToString() => Display;

    private static readonly Dictionary<string, string> DisplayByField = new(StringComparer.OrdinalIgnoreCase)
    {
        [EventLogFilterFields.EventId] = "Event ID equals",
        [EventLogFilterFields.SourceContains] = "Source contains",
        [EventLogFilterFields.MessageContains] = "Message contains"
    };

    public static string GetDisplay(string field) =>
        DisplayByField.TryGetValue(field, out var display) ? display : field;

    public static IReadOnlyList<EventLogFilterFieldOption> All { get; } = DisplayByField
        .Select(kvp => new EventLogFilterFieldOption(kvp.Key, kvp.Value))
        .ToList();
}

public sealed class TagItemViewModel : ObservableObject
{
    private string _key = string.Empty;
    private string _value = string.Empty;

    public TagItemViewModel()
    {
    }

    public TagItemViewModel(string key, string value)
    {
        _key = key;
        _value = value;
    }

    public string Key
    {
        get => _key;
        set => SetProperty(ref _key, value);
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}

public sealed class MetricPreviewDisplayRow
{
    public string Metric { get; set; } = string.Empty;
    public double Value { get; set; }
    public string Unit { get; set; } = string.Empty;
    public string TagsText { get; set; } = string.Empty;
}

public sealed class DataSourceFirewallHistoryRow
{
    public string TimeLocal { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}

public sealed class DataSourcesPageViewModel : PageViewModelBase
{
    private static readonly HashSet<string> DraftPersistedPropertyNames = new(StringComparer.Ordinal)
    {
        nameof(EventLogEnabled),
        nameof(EventLogPollIntervalSeconds),
        nameof(EventLogApplication),
        nameof(EventLogSystem),
        nameof(EventLogSecurity),
        nameof(EventLogSetup),
        nameof(LevelCritical),
        nameof(LevelError),
        nameof(LevelWarning),
        nameof(LevelInformation),
        nameof(LevelVerbose),
        nameof(EventLogPreviewCount),
        nameof(EventLogProviderNameOverride),
        nameof(EventLogServerNameOverride),
        nameof(IisEnabled),
        nameof(IisPollIntervalSeconds),
        nameof(IisSiteName),
        nameof(IisInclude4xx),
        nameof(IisInclude5xx),
        nameof(IisExcludeStaticFiles),
        nameof(IisPreviewCount),
        nameof(IisServerNameOverride),
        nameof(MetricsEnabled),
        nameof(MetricsPollIntervalSeconds),
        nameof(MetricsCpu),
        nameof(MetricsMemory),
        nameof(MetricsDisk),
        nameof(MetricsNetwork),
        nameof(MetricsServerNameOverride),
        nameof(HeartbeatEnabled),
        nameof(HeartbeatPollIntervalSeconds),
        nameof(HeartbeatMeasurement),
        nameof(HeartbeatCollection),
        nameof(HeartbeatIncludeUptime),
        nameof(HeartbeatIncludeHostnameTag),
        nameof(HeartbeatIncludeAppVersionTag),
        nameof(HeartbeatIncludeCpuPercent),
        nameof(HeartbeatIncludeMemoryPercent),
        nameof(HeartbeatServerNameOverride),
        nameof(HeartbeatEnvironmentOverride)
    };

    private static readonly HashSet<string> EventLogAutoApplyProperties = new(StringComparer.Ordinal)
    {
        nameof(EventLogEnabled), nameof(EventLogApplication), nameof(EventLogSystem),
        nameof(EventLogSecurity), nameof(EventLogSetup), nameof(LevelCritical),
        nameof(LevelError), nameof(LevelWarning), nameof(LevelInformation), nameof(LevelVerbose),
        nameof(EventLogProviderNameOverride), nameof(EventLogServerNameOverride)
    };

    private static readonly HashSet<string> IisAutoApplyProperties = new(StringComparer.Ordinal)
    {
        nameof(IisEnabled), nameof(IisInclude4xx), nameof(IisInclude5xx), nameof(IisExcludeStaticFiles),
        nameof(IisServerNameOverride)
    };

    private static readonly HashSet<string> MetricsAutoApplyProperties = new(StringComparer.Ordinal)
    {
        nameof(MetricsEnabled), nameof(MetricsCpu), nameof(MetricsMemory),
        nameof(MetricsDisk), nameof(MetricsNetwork), nameof(MetricsServerNameOverride)
    };

    private static readonly HashSet<string> HeartbeatAutoApplyProperties = new(StringComparer.Ordinal)
    {
        nameof(HeartbeatEnabled), nameof(HeartbeatIncludeUptime), nameof(HeartbeatIncludeHostnameTag),
        nameof(HeartbeatIncludeAppVersionTag), nameof(HeartbeatIncludeCpuPercent), nameof(HeartbeatIncludeMemoryPercent),
        nameof(HeartbeatServerNameOverride), nameof(HeartbeatEnvironmentOverride)
    };

    private readonly LocalCollectorAdminClient _adminClient;
    private readonly Action<string, bool> _statusCallback;
    private readonly Func<string, TestReportSession>? _openTestReport;
    private bool _suppressDraftPersistence;
    private CancellationTokenSource? _autoApplyCts;

    private bool _eventLogEnabled;
    private int _eventLogPollIntervalSeconds = 60;
    private bool _eventLogApplication = true;
    private bool _eventLogSystem = true;
    private bool _eventLogSecurity;
    private bool _eventLogSetup;
    private bool _levelCritical = true;
    private bool _levelError = true;
    private bool _levelWarning = true;
    private bool _levelInformation = true;
    private bool _levelVerbose;
    private string _newCustomChannel = string.Empty;
    private int _eventLogPreviewCount = 20;
    private string _eventLogValidationMessage = "-";
    private DateTime? _eventLogInitialStartDate;
    private bool _eventLogResumeFromLast = true;
    private EventLogFilterFieldOption _newEventLogFilterField = EventLogFilterFieldOption.All[0];
    private string _newEventLogFilterValue = string.Empty;
    private string _eventLogProviderNameOverride = string.Empty;
    private string _eventLogServerNameOverride = string.Empty;

    private bool _iisEnabled;
    private int _iisPollIntervalSeconds = 60;
    private string _newIisDirectory = string.Empty;
    private string _iisSiteName = string.Empty;
    private bool _iisInclude4xx = true;
    private bool _iisInclude5xx = true;
    private bool _iisExcludeStaticFiles;
    private int _iisPreviewCount = 20;
    private string _iisValidationMessage = "-";
    private DateTime? _iisInitialStartDate;
    private bool _iisResumeFromLast = true;
    private string _iisServerNameOverride = string.Empty;
    private IisFilterFieldOption _newIisFilterField = IisFilterFieldOption.All[0];
    private string _newIisFilterValue = string.Empty;

    private bool _metricsEnabled = true;
    private int _metricsPollIntervalSeconds = 60;
    private bool _metricsCpu = true;
    private bool _metricsMemory = true;
    private bool _metricsDisk = true;
    private bool _metricsNetwork = true;
    private string _metricsServerNameOverride = string.Empty;
    private string _newTagKey = string.Empty;
    private string _newTagValue = string.Empty;
    private string _metricsPreviewMessage = "-";
    private bool _heartbeatEnabled;
    private int _heartbeatPollIntervalSeconds = 60;
    private string _heartbeatMeasurement = "heartbeat";
    private string _heartbeatCollection = "beats";
    private bool _heartbeatIncludeUptime = true;
    private bool _heartbeatIncludeHostnameTag = true;
    private bool _heartbeatIncludeAppVersionTag;
    private bool _heartbeatIncludeCpuPercent;
    private bool _heartbeatIncludeMemoryPercent;
    private string _heartbeatServerNameOverride = string.Empty;
    private string _heartbeatEnvironmentOverride = string.Empty;
    private string _newHeartbeatTagKey = string.Empty;
    private string _newHeartbeatTagValue = string.Empty;
    private bool _heartbeatPaused;

    private bool _collectorConnected;
    private bool _eventLogPaused;
    private bool _iisPaused;
    private bool _metricsPaused;
    private string _firewallTabSummary = "Firewall: not loaded.";
    private string _firewallTabRuntime = "Runtime: unavailable.";
    private readonly SemaphoreSlim _firewallHistoryRefreshLock = new(1, 1);

    public DataSourcesPageViewModel(
        LocalCollectorAdminClient adminClient,
        Action<string, bool> statusCallback,
        Func<string, TestReportSession>? openTestReport = null)
        : base("Data Sources")
    {
        _adminClient = adminClient;
        _statusCallback = statusCallback;
        _openTestReport = openTestReport;

        CustomChannels = new ObservableCollection<StringItemViewModel>();
        EventLogPreviewRows = new ObservableCollection<EventLogPreviewRowDto>();
        EventLogFilterRules = new ObservableCollection<EventLogFilterRuleItemViewModel>();
        EventLogFilterRules.CollectionChanged += OnEventLogFilterRulesChanged;
        IisDirectories = new ObservableCollection<StringItemViewModel>();
        IisFilterRules = new ObservableCollection<IisFilterRuleItemViewModel>();
        IisFilterRules.CollectionChanged += OnIisFilterRulesChanged;
        IisPreviewRows = new ObservableCollection<IisPreviewRowDto>();
        MetricTags = new ObservableCollection<TagItemViewModel>();
        MetricsPreviewRows = new ObservableCollection<MetricPreviewDisplayRow>();
        HeartbeatTags = new ObservableCollection<TagItemViewModel>();
        FirewallHistoryRows = new ObservableCollection<DataSourceFirewallHistoryRow>();

        AddCustomChannelCommand = new RelayCommand(AddCustomChannel);
        RemoveCustomChannelCommand = new RelayCommand(RemoveSelectedCustomChannel);
        ValidateEventLogsCommand = new AsyncRelayCommand(ValidateEventLogsAsync);
        PreviewEventLogsCommand = new AsyncRelayCommand(PreviewEventLogsAsync);
        ApplyEventLogsCommand = new AsyncRelayCommand(ApplyEventLogsAsync);

        AddEventLogFilterRuleCommand = new RelayCommand(AddEventLogFilterRule);
        RemoveEventLogFilterRuleCommand = new RelayCommand<EventLogFilterRuleItemViewModel?>(RemoveEventLogFilterRule);
        ClearEventLogFilterRulesCommand = new RelayCommand(ClearEventLogFilterRules);
        ApplyEventLogPresetSecurityAuditCommand = new RelayCommand(ApplyEventLogPresetSecurityAudit);
        ApplyEventLogPresetServiceStateCommand = new RelayCommand(ApplyEventLogPresetServiceState);
        ApplyEventLogPresetDcomCommand = new RelayCommand(ApplyEventLogPresetDcom);

        AddIisDirectoryCommand = new RelayCommand(AddIisDirectory);
        RemoveIisDirectoryCommand = new RelayCommand(RemoveSelectedIisDirectory);
        ValidateIisCommand = new AsyncRelayCommand(ValidateIisAsync);
        PreviewIisCommand = new AsyncRelayCommand(PreviewIisAsync);
        ApplyIisCommand = new AsyncRelayCommand(ApplyIisAsync);

        AddIisFilterRuleCommand = new RelayCommand(AddIisFilterRule);
        RemoveIisFilterRuleCommand = new RelayCommand<IisFilterRuleItemViewModel?>(RemoveIisFilterRule);
        ClearIisFilterRulesCommand = new RelayCommand(ClearIisFilterRules);
        ApplyPresetHealthCommand = new RelayCommand(ApplyPresetHealth);
        ApplyPresetOptionsPreflightCommand = new RelayCommand(ApplyPresetOptionsPreflight);
        ApplyPresetBotsCommand = new RelayCommand(ApplyPresetBots);
        ApplyPresetOAuthCallbacksCommand = new RelayCommand(ApplyPresetOAuthCallbacks);
        ApplyPresetVerySlowCommand = new RelayCommand(ApplyPresetVerySlow);

        AddTagCommand = new RelayCommand(AddTag);
        RemoveTagCommand = new RelayCommand(RemoveSelectedTag);
        PreviewMetricsCommand = new AsyncRelayCommand(PreviewMetricsAsync);
        ApplyMetricsCommand = new AsyncRelayCommand(ApplyMetricsAsync);
        RefreshFirewallHistoryCommand = new AsyncRelayCommand(RefreshFirewallHistoryAsync);

        PauseEventLogCommand = new AsyncRelayCommand(() => ToggleModuleAsync("EventLog", false));
        ResumeEventLogCommand = new AsyncRelayCommand(() => ToggleModuleAsync("EventLog", true));
        PauseIisCommand = new AsyncRelayCommand(() => ToggleModuleAsync("IIS", false));
        ResumeIisCommand = new AsyncRelayCommand(() => ToggleModuleAsync("IIS", true));
        PauseMetricsCommand = new AsyncRelayCommand(() => ToggleModuleAsync("Metrics", false));
        ResumeMetricsCommand = new AsyncRelayCommand(() => ToggleModuleAsync("Metrics", true));

        AddHeartbeatTagCommand = new RelayCommand(AddHeartbeatTag);
        RemoveHeartbeatTagCommand = new RelayCommand(RemoveSelectedHeartbeatTag);
        ApplyHeartbeatCommand = new AsyncRelayCommand(ApplyHeartbeatAsync);
        PauseHeartbeatCommand = new AsyncRelayCommand(() => ToggleModuleAsync("Heartbeat", false));
        ResumeHeartbeatCommand = new AsyncRelayCommand(() => ToggleModuleAsync("Heartbeat", true));

        TestEventLogCommand = new AsyncRelayCommand(() => SendTestLogAsync("EventLog"));
        TestIisCommand = new AsyncRelayCommand(() => SendTestLogAsync("IIS"));
        TestMetricsCommand = new AsyncRelayCommand(() => SendTestLogAsync("Metrics"));
        TestHeartbeatCommand = new AsyncRelayCommand(() => SendTestLogAsync("Heartbeat"));

        PropertyChanged += OnDataSourcesPropertyChanged;
    }

    public ObservableCollection<StringItemViewModel> CustomChannels { get; }
    public ObservableCollection<EventLogPreviewRowDto> EventLogPreviewRows { get; }
    public ObservableCollection<EventLogFilterRuleItemViewModel> EventLogFilterRules { get; }
    public IReadOnlyList<EventLogFilterFieldOption> EventLogFilterFieldOptions => EventLogFilterFieldOption.All;
    public ObservableCollection<StringItemViewModel> IisDirectories { get; }
    public ObservableCollection<IisFilterRuleItemViewModel> IisFilterRules { get; }
    public ObservableCollection<IisPreviewRowDto> IisPreviewRows { get; }
    public IReadOnlyList<IisFilterFieldOption> IisFilterFieldOptions => IisFilterFieldOption.All;
    public ObservableCollection<TagItemViewModel> MetricTags { get; }
    public ObservableCollection<MetricPreviewDisplayRow> MetricsPreviewRows { get; }
    public ObservableCollection<TagItemViewModel> HeartbeatTags { get; }
    public ObservableCollection<DataSourceFirewallHistoryRow> FirewallHistoryRows { get; }

    public StringItemViewModel? SelectedCustomChannel { get; set; }
    public StringItemViewModel? SelectedIisDirectory { get; set; }
    public TagItemViewModel? SelectedTag { get; set; }
    public TagItemViewModel? SelectedHeartbeatTag { get; set; }

    public bool EventLogEnabled
    {
        get => _eventLogEnabled;
        set => SetProperty(ref _eventLogEnabled, value);
    }

    public int EventLogPollIntervalSeconds
    {
        get => _eventLogPollIntervalSeconds;
        set => SetProperty(ref _eventLogPollIntervalSeconds, value);
    }

    public bool EventLogApplication
    {
        get => _eventLogApplication;
        set => SetProperty(ref _eventLogApplication, value);
    }

    public bool EventLogSystem
    {
        get => _eventLogSystem;
        set => SetProperty(ref _eventLogSystem, value);
    }

    public bool EventLogSecurity
    {
        get => _eventLogSecurity;
        set => SetProperty(ref _eventLogSecurity, value);
    }

    public bool EventLogSetup
    {
        get => _eventLogSetup;
        set => SetProperty(ref _eventLogSetup, value);
    }

    public bool LevelCritical
    {
        get => _levelCritical;
        set => SetProperty(ref _levelCritical, value);
    }

    public bool LevelError
    {
        get => _levelError;
        set => SetProperty(ref _levelError, value);
    }

    public bool LevelWarning
    {
        get => _levelWarning;
        set => SetProperty(ref _levelWarning, value);
    }

    public bool LevelInformation
    {
        get => _levelInformation;
        set => SetProperty(ref _levelInformation, value);
    }

    public bool LevelVerbose
    {
        get => _levelVerbose;
        set => SetProperty(ref _levelVerbose, value);
    }

    public string NewCustomChannel
    {
        get => _newCustomChannel;
        set => SetProperty(ref _newCustomChannel, value);
    }

    public int EventLogPreviewCount
    {
        get => _eventLogPreviewCount;
        set => SetProperty(ref _eventLogPreviewCount, value);
    }

    public string EventLogValidationMessage
    {
        get => _eventLogValidationMessage;
        set => SetProperty(ref _eventLogValidationMessage, value);
    }

    public DateTime? EventLogInitialStartDate
    {
        get => _eventLogInitialStartDate;
        set => SetProperty(ref _eventLogInitialStartDate, value);
    }

    public bool EventLogResumeFromLast
    {
        get => _eventLogResumeFromLast;
        set
        {
            if (!SetProperty(ref _eventLogResumeFromLast, value)) return;
            if (value)
                EventLogInitialStartDate = null;
            else
                EventLogInitialStartDate ??= DateTime.UtcNow.Date;
        }
    }

    public EventLogFilterFieldOption NewEventLogFilterField
    {
        get => _newEventLogFilterField;
        set => SetProperty(ref _newEventLogFilterField, value ?? EventLogFilterFieldOption.All[0]);
    }

    public string NewEventLogFilterValue
    {
        get => _newEventLogFilterValue;
        set => SetProperty(ref _newEventLogFilterValue, value);
    }

    /// <summary>
    /// Optional Provider (Source) name override applied to every ingested EventLog
    /// row — both real Windows events and the synthetic Test event. Useful for
    /// identifying which server / collector instance the rows are coming from.
    /// Blank = keep original Provider (Test falls back to "LogDB.UI.Test").
    /// </summary>
    public string EventLogProviderNameOverride
    {
        get => _eventLogProviderNameOverride;
        set => SetProperty(ref _eventLogProviderNameOverride, value ?? string.Empty);
    }

    /// <summary>
    /// Optional per-module Server name override for the EventLog tab. When set,
    /// every emitted row (real + Test) carries this value as Computer + the
    /// serverName attribute. The original raw machine name is preserved on each
    /// row as the 'original_computer' attribute so it stays queryable.
    /// </summary>
    public string EventLogServerNameOverride
    {
        get => _eventLogServerNameOverride;
        set => SetProperty(ref _eventLogServerNameOverride, value ?? string.Empty);
    }

    public bool IisEnabled
    {
        get => _iisEnabled;
        set => SetProperty(ref _iisEnabled, value);
    }

    public int IisPollIntervalSeconds
    {
        get => _iisPollIntervalSeconds;
        set => SetProperty(ref _iisPollIntervalSeconds, value);
    }

    public string NewIisDirectory
    {
        get => _newIisDirectory;
        set => SetProperty(ref _newIisDirectory, value);
    }

    public string IisSiteName
    {
        get => _iisSiteName;
        set => SetProperty(ref _iisSiteName, value);
    }

    public bool IisInclude4xx
    {
        get => _iisInclude4xx;
        set => SetProperty(ref _iisInclude4xx, value);
    }

    public bool IisInclude5xx
    {
        get => _iisInclude5xx;
        set => SetProperty(ref _iisInclude5xx, value);
    }

    public bool IisExcludeStaticFiles
    {
        get => _iisExcludeStaticFiles;
        set => SetProperty(ref _iisExcludeStaticFiles, value);
    }

    /// <summary>
    /// Optional Server name override applied to IIS rows only. Replaces
    /// Server:ServerName for the IIS module. Blank = use global server name.
    /// </summary>
    public string IisServerNameOverride
    {
        get => _iisServerNameOverride;
        set => SetProperty(ref _iisServerNameOverride, value ?? string.Empty);
    }

    public IisFilterFieldOption NewIisFilterField
    {
        get => _newIisFilterField;
        set => SetProperty(ref _newIisFilterField, value ?? IisFilterFieldOption.All[0]);
    }

    public string NewIisFilterValue
    {
        get => _newIisFilterValue;
        set => SetProperty(ref _newIisFilterValue, value);
    }

    public int IisPreviewCount
    {
        get => _iisPreviewCount;
        set => SetProperty(ref _iisPreviewCount, value);
    }

    public string IisValidationMessage
    {
        get => _iisValidationMessage;
        set => SetProperty(ref _iisValidationMessage, value);
    }

    public DateTime? IisInitialStartDate
    {
        get => _iisInitialStartDate;
        set => SetProperty(ref _iisInitialStartDate, value);
    }

    public bool IisResumeFromLast
    {
        get => _iisResumeFromLast;
        set
        {
            if (!SetProperty(ref _iisResumeFromLast, value)) return;
            if (value)
                IisInitialStartDate = null;
            else
                IisInitialStartDate ??= DateTime.UtcNow.Date;
        }
    }

    public bool MetricsEnabled
    {
        get => _metricsEnabled;
        set => SetProperty(ref _metricsEnabled, value);
    }

    public int MetricsPollIntervalSeconds
    {
        get => _metricsPollIntervalSeconds;
        set => SetProperty(ref _metricsPollIntervalSeconds, value);
    }

    public bool MetricsCpu
    {
        get => _metricsCpu;
        set => SetProperty(ref _metricsCpu, value);
    }

    public bool MetricsMemory
    {
        get => _metricsMemory;
        set => SetProperty(ref _metricsMemory, value);
    }

    public bool MetricsDisk
    {
        get => _metricsDisk;
        set => SetProperty(ref _metricsDisk, value);
    }

    public bool MetricsNetwork
    {
        get => _metricsNetwork;
        set => SetProperty(ref _metricsNetwork, value);
    }

    /// <summary>
    /// Optional Server name override applied to metric rows only. Replaces
    /// Server:ServerName for the WindowsTracker module. Blank = use the global
    /// server name from Destination.
    /// </summary>
    public string MetricsServerNameOverride
    {
        get => _metricsServerNameOverride;
        set => SetProperty(ref _metricsServerNameOverride, value ?? string.Empty);
    }

    public bool HeartbeatEnabled
    {
        get => _heartbeatEnabled;
        set => SetProperty(ref _heartbeatEnabled, value);
    }

    public int HeartbeatPollIntervalSeconds
    {
        get => _heartbeatPollIntervalSeconds;
        set => SetProperty(ref _heartbeatPollIntervalSeconds, value);
    }

    public string HeartbeatMeasurement
    {
        get => _heartbeatMeasurement;
        set => SetProperty(ref _heartbeatMeasurement, value);
    }

    public string HeartbeatCollection
    {
        get => _heartbeatCollection;
        set => SetProperty(ref _heartbeatCollection, value);
    }

    public bool HeartbeatIncludeUptime
    {
        get => _heartbeatIncludeUptime;
        set => SetProperty(ref _heartbeatIncludeUptime, value);
    }

    public bool HeartbeatIncludeHostnameTag
    {
        get => _heartbeatIncludeHostnameTag;
        set => SetProperty(ref _heartbeatIncludeHostnameTag, value);
    }

    public bool HeartbeatIncludeAppVersionTag
    {
        get => _heartbeatIncludeAppVersionTag;
        set => SetProperty(ref _heartbeatIncludeAppVersionTag, value);
    }

    public bool HeartbeatIncludeCpuPercent
    {
        get => _heartbeatIncludeCpuPercent;
        set => SetProperty(ref _heartbeatIncludeCpuPercent, value);
    }

    public bool HeartbeatIncludeMemoryPercent
    {
        get => _heartbeatIncludeMemoryPercent;
        set => SetProperty(ref _heartbeatIncludeMemoryPercent, value);
    }

    /// <summary>
    /// Optional Server name override applied to every emitted beat (affects the
    /// "host" tag) and the Heartbeat Test. Blank = use global server name.
    /// </summary>
    public string HeartbeatServerNameOverride
    {
        get => _heartbeatServerNameOverride;
        set => SetProperty(ref _heartbeatServerNameOverride, value ?? string.Empty);
    }

    /// <summary>
    /// Optional Environment override applied to every emitted beat and the
    /// Heartbeat Test. Blank = use global Environment.
    /// </summary>
    public string HeartbeatEnvironmentOverride
    {
        get => _heartbeatEnvironmentOverride;
        set => SetProperty(ref _heartbeatEnvironmentOverride, value ?? string.Empty);
    }

    public string NewHeartbeatTagKey
    {
        get => _newHeartbeatTagKey;
        set => SetProperty(ref _newHeartbeatTagKey, value);
    }

    public string NewHeartbeatTagValue
    {
        get => _newHeartbeatTagValue;
        set => SetProperty(ref _newHeartbeatTagValue, value);
    }

    public string NewTagKey
    {
        get => _newTagKey;
        set => SetProperty(ref _newTagKey, value);
    }

    public string NewTagValue
    {
        get => _newTagValue;
        set => SetProperty(ref _newTagValue, value);
    }

    public string MetricsPreviewMessage
    {
        get => _metricsPreviewMessage;
        set => SetProperty(ref _metricsPreviewMessage, value);
    }

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

    public bool EventLogPaused
    {
        get => _eventLogPaused;
        set
        {
            if (!SetProperty(ref _eventLogPaused, value)) return;
            NotifyPropertyChanged(nameof(EventLogTabHeader));
            NotifyPropertyChanged(nameof(EventLogConfigEditable));
        }
    }

    public bool IisPaused
    {
        get => _iisPaused;
        set
        {
            if (!SetProperty(ref _iisPaused, value)) return;
            NotifyPropertyChanged(nameof(IisTabHeader));
            NotifyPropertyChanged(nameof(IisConfigEditable));
        }
    }

    public bool MetricsPaused
    {
        get => _metricsPaused;
        set
        {
            if (!SetProperty(ref _metricsPaused, value)) return;
            NotifyPropertyChanged(nameof(MetricsTabHeader));
            NotifyPropertyChanged(nameof(MetricsConfigEditable));
        }
    }

    public bool HeartbeatPaused
    {
        get => _heartbeatPaused;
        set
        {
            if (!SetProperty(ref _heartbeatPaused, value)) return;
            NotifyPropertyChanged(nameof(HeartbeatTabHeader));
            NotifyPropertyChanged(nameof(HeartbeatConfigEditable));
        }
    }

    public string EventLogTabHeader => _eventLogPaused ? "Windows Event Logs (PAUSED)" : "Windows Event Logs";
    public string IisTabHeader => _iisPaused ? "IIS Logs (PAUSED)" : "IIS Logs";
    public string MetricsTabHeader => _metricsPaused ? "Windows Metrics (PAUSED)" : "Windows Metrics";
    public string HeartbeatTabHeader => _heartbeatPaused ? "Heartbeat (PAUSED)" : "Heartbeat";

    public bool EventLogConfigEditable => !_collectorConnected || _eventLogPaused;
    public bool IisConfigEditable => !_collectorConnected || _iisPaused;
    public bool MetricsConfigEditable => !_collectorConnected || _metricsPaused;
    public bool HeartbeatConfigEditable => !_collectorConnected || _heartbeatPaused;

    /// <summary>
    /// True only when a collector service or console instance is reachable on the
    /// named-pipe control channel. Used to gate Pause/Resume buttons (no point
    /// pausing a service that isn't running).
    /// </summary>
    public bool CollectorConnected => _collectorConnected;

    public RelayCommand AddCustomChannelCommand { get; }
    public RelayCommand RemoveCustomChannelCommand { get; }
    public AsyncRelayCommand ValidateEventLogsCommand { get; }
    public AsyncRelayCommand PreviewEventLogsCommand { get; }
    public AsyncRelayCommand ApplyEventLogsCommand { get; }
    public RelayCommand AddEventLogFilterRuleCommand { get; }
    public RelayCommand<EventLogFilterRuleItemViewModel?> RemoveEventLogFilterRuleCommand { get; }
    public RelayCommand ClearEventLogFilterRulesCommand { get; }
    public RelayCommand ApplyEventLogPresetSecurityAuditCommand { get; }
    public RelayCommand ApplyEventLogPresetServiceStateCommand { get; }
    public RelayCommand ApplyEventLogPresetDcomCommand { get; }

    public RelayCommand AddIisDirectoryCommand { get; }
    public RelayCommand RemoveIisDirectoryCommand { get; }
    public AsyncRelayCommand ValidateIisCommand { get; }
    public AsyncRelayCommand PreviewIisCommand { get; }
    public AsyncRelayCommand ApplyIisCommand { get; }
    public RelayCommand AddIisFilterRuleCommand { get; }
    public RelayCommand<IisFilterRuleItemViewModel?> RemoveIisFilterRuleCommand { get; }
    public RelayCommand ClearIisFilterRulesCommand { get; }
    public RelayCommand ApplyPresetHealthCommand { get; }
    public RelayCommand ApplyPresetOptionsPreflightCommand { get; }
    public RelayCommand ApplyPresetBotsCommand { get; }
    public RelayCommand ApplyPresetOAuthCallbacksCommand { get; }
    public RelayCommand ApplyPresetVerySlowCommand { get; }

    public RelayCommand AddTagCommand { get; }
    public RelayCommand RemoveTagCommand { get; }
    public AsyncRelayCommand PreviewMetricsCommand { get; }
    public AsyncRelayCommand ApplyMetricsCommand { get; }
    public AsyncRelayCommand RefreshFirewallHistoryCommand { get; }

    public AsyncRelayCommand PauseEventLogCommand { get; }
    public AsyncRelayCommand ResumeEventLogCommand { get; }
    public AsyncRelayCommand PauseIisCommand { get; }
    public AsyncRelayCommand ResumeIisCommand { get; }
    public AsyncRelayCommand PauseMetricsCommand { get; }
    public AsyncRelayCommand ResumeMetricsCommand { get; }

    public RelayCommand AddHeartbeatTagCommand { get; }
    public RelayCommand RemoveHeartbeatTagCommand { get; }
    public AsyncRelayCommand ApplyHeartbeatCommand { get; }
    public AsyncRelayCommand PauseHeartbeatCommand { get; }
    public AsyncRelayCommand ResumeHeartbeatCommand { get; }

    public AsyncRelayCommand TestEventLogCommand { get; }
    public AsyncRelayCommand TestIisCommand { get; }
    public AsyncRelayCommand TestMetricsCommand { get; }
    public AsyncRelayCommand TestHeartbeatCommand { get; }

    public override async Task RefreshAsync()
    {
        _suppressDraftPersistence = true;
        try
        {
            var config = _adminClient.SnapshotWorkingConfig();

            var eventLog = config.Modules.EventLog;
            EventLogEnabled = eventLog.Enabled;
            EventLogPollIntervalSeconds = eventLog.PollIntervalSeconds;
            EventLogApplication = eventLog.SourcesChannels.Any(channel => channel.Equals("Application", StringComparison.OrdinalIgnoreCase));
            EventLogSystem = eventLog.SourcesChannels.Any(channel => channel.Equals("System", StringComparison.OrdinalIgnoreCase));
            EventLogSecurity = eventLog.SourcesChannels.Any(channel => channel.Equals("Security", StringComparison.OrdinalIgnoreCase));
            EventLogSetup = eventLog.SourcesChannels.Any(channel => channel.Equals("Setup", StringComparison.OrdinalIgnoreCase));

            LevelCritical = eventLog.LevelFilters.Any(level => level.Equals("critical", StringComparison.OrdinalIgnoreCase));
            LevelError = eventLog.LevelFilters.Any(level => level.Equals("error", StringComparison.OrdinalIgnoreCase));
            LevelWarning = eventLog.LevelFilters.Any(level => level.Equals("warning", StringComparison.OrdinalIgnoreCase));
            LevelInformation = eventLog.LevelFilters.Any(level => level.Equals("information", StringComparison.OrdinalIgnoreCase));
            LevelVerbose = eventLog.LevelFilters.Any(level => level.Equals("verbose", StringComparison.OrdinalIgnoreCase));
            EventLogResumeFromLast = !eventLog.ResetState && !eventLog.InitialStartDateUtc.HasValue;
            EventLogInitialStartDate = eventLog.InitialStartDateUtc.HasValue
                ? eventLog.InitialStartDateUtc.Value
                : EventLogResumeFromLast ? null : DateTime.UtcNow.Date;
            EventLogProviderNameOverride = eventLog.ProviderNameOverride ?? string.Empty;
            EventLogServerNameOverride = eventLog.ServerNameOverride ?? string.Empty;

            CustomChannels.Clear();
            foreach (var channel in eventLog.SourcesChannels
                         .Where(channel => !IsPresetChannel(channel))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                CustomChannels.Add(new StringItemViewModel(channel));
            }

            EventLogFilterRules.Clear();
            foreach (var rule in eventLog.FilterRules ?? Enumerable.Empty<EventLogFilterRuleDto>())
            {
                if (string.IsNullOrWhiteSpace(rule.Value)) continue;
                EventLogFilterRules.Add(new EventLogFilterRuleItemViewModel(rule.Field, rule.Value, rule.Enabled));
            }

            var iis = config.Modules.IIS;
            IisEnabled = iis.Enabled;
            IisPollIntervalSeconds = iis.PollIntervalSeconds;
            IisSiteName = iis.SiteName ?? string.Empty;
            IisInclude4xx = iis.Include4xx;
            IisInclude5xx = iis.Include5xx;
            IisExcludeStaticFiles = iis.ExcludeStaticFiles;
            IisServerNameOverride = iis.ServerNameOverride ?? string.Empty;
            IisResumeFromLast = !iis.ResetState && !iis.InitialStartDateUtc.HasValue;
            IisInitialStartDate = iis.InitialStartDateUtc.HasValue
                ? iis.InitialStartDateUtc.Value
                : IisResumeFromLast ? null : DateTime.UtcNow.Date;
            IisDirectories.Clear();
            foreach (var path in iis.LogDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                IisDirectories.Add(new StringItemViewModel(path));
            }

            IisFilterRules.Clear();
            foreach (var rule in iis.FilterRules ?? Enumerable.Empty<IisFilterRuleDto>())
            {
                if (string.IsNullOrWhiteSpace(rule.Value)) continue;
                IisFilterRules.Add(new IisFilterRuleItemViewModel(rule.Field, rule.Value, rule.Enabled));
            }

            var metrics = config.Modules.Metrics;
            MetricsEnabled = metrics.Enabled;
            MetricsPollIntervalSeconds = metrics.PollIntervalSeconds;
            MetricsCpu = metrics.IncludeCpu;
            MetricsMemory = metrics.IncludeMemory;
            MetricsDisk = metrics.IncludeDisk;
            MetricsNetwork = metrics.IncludeNetwork;
            MetricsServerNameOverride = metrics.ServerNameOverride ?? string.Empty;
            foreach (var tag in MetricTags)
            {
                tag.PropertyChanged -= OnTagItemPropertyChanged;
            }

            MetricTags.Clear();
            foreach (var tag in metrics.Tags.OrderBy(tag => tag.Key, StringComparer.OrdinalIgnoreCase))
            {
                var item = new TagItemViewModel(tag.Key, tag.Value);
                item.PropertyChanged += OnTagItemPropertyChanged;
                MetricTags.Add(item);
            }

            var heartbeat = config.Modules.Heartbeat;
            HeartbeatEnabled = heartbeat.Enabled;
            HeartbeatPollIntervalSeconds = heartbeat.PollIntervalSeconds;
            HeartbeatMeasurement = string.IsNullOrWhiteSpace(heartbeat.Measurement) ? "heartbeat" : heartbeat.Measurement;
            HeartbeatCollection = string.IsNullOrWhiteSpace(heartbeat.Collection) ? "beats" : heartbeat.Collection;
            HeartbeatIncludeUptime = heartbeat.IncludeUptime;
            HeartbeatIncludeHostnameTag = heartbeat.IncludeHostnameTag;
            HeartbeatIncludeAppVersionTag = heartbeat.IncludeAppVersionTag;
            HeartbeatIncludeCpuPercent = heartbeat.IncludeCpuPercent;
            HeartbeatIncludeMemoryPercent = heartbeat.IncludeMemoryPercent;
            HeartbeatServerNameOverride = heartbeat.ServerNameOverride ?? string.Empty;
            HeartbeatEnvironmentOverride = heartbeat.EnvironmentOverride ?? string.Empty;

            foreach (var tag in HeartbeatTags)
            {
                tag.PropertyChanged -= OnHeartbeatTagItemPropertyChanged;
            }

            HeartbeatTags.Clear();
            foreach (var tag in heartbeat.Tags.OrderBy(tag => tag.Key, StringComparer.OrdinalIgnoreCase))
            {
                var item = new TagItemViewModel(tag.Key, tag.Value);
                item.PropertyChanged += OnHeartbeatTagItemPropertyChanged;
                HeartbeatTags.Add(item);
            }

            var draft = WindowPlacementStore.LoadDataSourcesDraft();
            if (draft != null)
            {
                ApplyDraft(draft);
            }
        }
        finally
        {
            _suppressDraftPersistence = false;
        }

        await RefreshFirewallSummaryAsync();
        await RefreshFirewallHistoryAsync();
        await RefreshModulePausedStatesAsync();
    }

    public void AddIisDirectoryFromPicker(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        NewIisDirectory = path.Trim();
        AddIisDirectory();
    }

    private async Task ApplyEventLogsAsync()
    {
        var config = _adminClient.SnapshotWorkingConfig();
        config.Modules.EventLog.Enabled = EventLogEnabled;
        config.Modules.EventLog.PollIntervalSeconds = Math.Max(5, EventLogPollIntervalSeconds);
        config.Modules.EventLog.SourcesChannels = BuildEventLogChannels();
        config.Modules.EventLog.LevelFilters = BuildEventLogLevels();
        config.Modules.EventLog.FilterRules = EventLogFilterRules
            .Where(r => !string.IsNullOrWhiteSpace(r.Value))
            .Select(r => new EventLogFilterRuleDto { Field = r.Field, Value = r.Value.Trim(), Enabled = r.Enabled })
            .ToList();
        config.Modules.EventLog.ResetState = !EventLogResumeFromLast;
        config.Modules.EventLog.InitialStartDateUtc = EventLogResumeFromLast ? null : EventLogInitialStartDate;
        config.Modules.EventLog.ProviderNameOverride = string.IsNullOrWhiteSpace(EventLogProviderNameOverride)
            ? null
            : EventLogProviderNameOverride.Trim();
        config.Modules.EventLog.ServerNameOverride = string.IsNullOrWhiteSpace(EventLogServerNameOverride)
            ? null
            : EventLogServerNameOverride.Trim();

        var result = await _adminClient.ApplyConfigAsync(config);
        _statusCallback(result.Message, result.Success);
        if (result.Success)
        {
            PersistDraftIfReady();
            await RefreshAsync();
        }
    }

    private async Task ApplyIisAsync()
    {
        var config = _adminClient.SnapshotWorkingConfig();
        config.Modules.IIS.Enabled = IisEnabled;
        config.Modules.IIS.PollIntervalSeconds = Math.Max(5, IisPollIntervalSeconds);
        config.Modules.IIS.LogDirectories = IisDirectories
            .Select(item => item.Value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        config.Modules.IIS.SiteName = string.IsNullOrWhiteSpace(IisSiteName) ? null : IisSiteName.Trim();
        config.Modules.IIS.Include4xx = IisInclude4xx;
        config.Modules.IIS.Include5xx = IisInclude5xx;
        config.Modules.IIS.ExcludeStaticFiles = IisExcludeStaticFiles;
        config.Modules.IIS.FilterRules = IisFilterRules
            .Where(r => !string.IsNullOrWhiteSpace(r.Value))
            .Select(r => new IisFilterRuleDto { Field = r.Field, Value = r.Value.Trim(), Enabled = r.Enabled })
            .ToList();
        config.Modules.IIS.ResetState = !IisResumeFromLast;
        config.Modules.IIS.InitialStartDateUtc = IisResumeFromLast ? null : IisInitialStartDate;
        config.Modules.IIS.ServerNameOverride = string.IsNullOrWhiteSpace(IisServerNameOverride)
            ? null
            : IisServerNameOverride.Trim();

        var result = await _adminClient.ApplyConfigAsync(config);
        _statusCallback(result.Message, result.Success);
        if (result.Success)
        {
            PersistDraftIfReady();
            await RefreshAsync();
        }
    }

    private async Task ApplyMetricsAsync()
    {
        var config = _adminClient.SnapshotWorkingConfig();
        config.Modules.Metrics.Enabled = MetricsEnabled;
        config.Modules.Metrics.PollIntervalSeconds = Math.Max(5, MetricsPollIntervalSeconds);
        config.Modules.Metrics.IncludeCpu = MetricsCpu;
        config.Modules.Metrics.IncludeMemory = MetricsMemory;
        config.Modules.Metrics.IncludeDisk = MetricsDisk;
        config.Modules.Metrics.IncludeNetwork = MetricsNetwork;
        config.Modules.Metrics.ServerNameOverride = string.IsNullOrWhiteSpace(MetricsServerNameOverride)
            ? null
            : MetricsServerNameOverride.Trim();
        config.Modules.Metrics.Tags = MetricTags
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .ToDictionary(item => item.Key.Trim(), item => item.Value.Trim(), StringComparer.OrdinalIgnoreCase);

        var result = await _adminClient.ApplyConfigAsync(config);
        _statusCallback(result.Message, result.Success);
        if (result.Success)
        {
            PersistDraftIfReady();
            await RefreshAsync();
        }
    }

    private async Task ApplyHeartbeatAsync()
    {
        var config = _adminClient.SnapshotWorkingConfig();
        config.Modules.Heartbeat.Enabled = HeartbeatEnabled;
        config.Modules.Heartbeat.PollIntervalSeconds = Math.Max(5, HeartbeatPollIntervalSeconds);
        config.Modules.Heartbeat.Measurement = string.IsNullOrWhiteSpace(HeartbeatMeasurement)
            ? "heartbeat" : HeartbeatMeasurement.Trim();
        config.Modules.Heartbeat.Collection = string.IsNullOrWhiteSpace(HeartbeatCollection)
            ? "beats" : HeartbeatCollection.Trim();
        config.Modules.Heartbeat.IncludeUptime = HeartbeatIncludeUptime;
        config.Modules.Heartbeat.IncludeHostnameTag = HeartbeatIncludeHostnameTag;
        config.Modules.Heartbeat.IncludeAppVersionTag = HeartbeatIncludeAppVersionTag;
        config.Modules.Heartbeat.IncludeCpuPercent = HeartbeatIncludeCpuPercent;
        config.Modules.Heartbeat.IncludeMemoryPercent = HeartbeatIncludeMemoryPercent;
        config.Modules.Heartbeat.ServerNameOverride = string.IsNullOrWhiteSpace(HeartbeatServerNameOverride)
            ? null
            : HeartbeatServerNameOverride.Trim();
        config.Modules.Heartbeat.EnvironmentOverride = string.IsNullOrWhiteSpace(HeartbeatEnvironmentOverride)
            ? null
            : HeartbeatEnvironmentOverride.Trim();
        config.Modules.Heartbeat.Tags = HeartbeatTags
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .ToDictionary(item => item.Key.Trim(), item => item.Value.Trim(), StringComparer.OrdinalIgnoreCase);

        var result = await _adminClient.ApplyConfigAsync(config);
        _statusCallback(result.Message, result.Success);
        if (result.Success)
        {
            PersistDraftIfReady();
            await RefreshAsync();
        }
    }

    private async Task ValidateEventLogsAsync()
    {
        var result = await _adminClient.ValidateEventLogAccessAsync();
        EventLogValidationMessage = result.Message;
        _statusCallback(result.Message, result.Success);
    }

    private async Task PreviewEventLogsAsync()
    {
        EventLogPreviewRows.Clear();
        var preview = await _adminClient.PreviewEventLogsAsync(EventLogPreviewCount);
        foreach (var row in preview.Rows)
        {
            EventLogPreviewRows.Add(row);
        }

        _statusCallback(preview.Message, preview.Success);
    }

    private async Task ValidateIisAsync()
    {
        var result = await _adminClient.ValidateIisPathsAsync();
        IisValidationMessage = result.Message;
        _statusCallback(result.Message, result.Success);
    }

    private async Task PreviewIisAsync()
    {
        IisPreviewRows.Clear();
        var preview = await _adminClient.PreviewIisLogsAsync(IisPreviewCount);
        foreach (var row in preview.Rows)
        {
            IisPreviewRows.Add(row);
        }

        _statusCallback(preview.Message, preview.Success);
    }

    private async Task PreviewMetricsAsync()
    {
        MetricsPreviewRows.Clear();
        var preview = await _adminClient.PreviewMetricsAsync();
        foreach (var row in preview.Rows)
        {
            MetricsPreviewRows.Add(new MetricPreviewDisplayRow
            {
                Metric = row.Metric,
                Value = row.Value,
                Unit = row.Unit,
                TagsText = row.Tags.Count == 0
                    ? "-"
                    : string.Join(", ", row.Tags.Select(tag => $"{tag.Key}={tag.Value}"))
            });
        }

        MetricsPreviewMessage = preview.Message;
        _statusCallback(preview.Message, preview.Success);
    }

    private void AddCustomChannel()
    {
        var channel = NewCustomChannel.Trim();
        if (string.IsNullOrWhiteSpace(channel))
        {
            return;
        }

        var exists = CustomChannels.Any(item => item.Value.Equals(channel, StringComparison.OrdinalIgnoreCase));
        if (!exists && !IsPresetChannel(channel))
        {
            CustomChannels.Add(new StringItemViewModel(channel));
            PersistDraftIfReady();
        }

        NewCustomChannel = string.Empty;
    }

    private void RemoveSelectedCustomChannel()
    {
        if (SelectedCustomChannel != null)
        {
            CustomChannels.Remove(SelectedCustomChannel);
            PersistDraftIfReady();
        }
    }

    private void AddIisDirectory()
    {
        var path = NewIisDirectory.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!IisDirectories.Any(item => item.Value.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            IisDirectories.Add(new StringItemViewModel(path));
            PersistDraftIfReady();
        }

        NewIisDirectory = string.Empty;
    }

    private void RemoveSelectedIisDirectory()
    {
        if (SelectedIisDirectory != null)
        {
            IisDirectories.Remove(SelectedIisDirectory);
            PersistDraftIfReady();
        }
    }

    private void OnIisFilterRulesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (IisFilterRuleItemViewModel old in e.OldItems)
            {
                old.PropertyChanged -= OnIisFilterRuleItemChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (IisFilterRuleItemViewModel @new in e.NewItems)
            {
                @new.PropertyChanged += OnIisFilterRuleItemChanged;
            }
        }
        PersistDraftIfReady();
        if (!_suppressDraftPersistence)
        {
            ScheduleAutoApply(ApplyIisAsync);
        }
    }

    private void OnIisFilterRuleItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        PersistDraftIfReady();
        if (!_suppressDraftPersistence)
        {
            ScheduleAutoApply(ApplyIisAsync);
        }
    }

    private void AddIisFilterRule()
    {
        var value = NewIisFilterValue.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var field = NewIisFilterField?.Field ?? IisFilterFields.PathPrefix;

        if (IisFilterRules.Any(r =>
                r.Field.Equals(field, StringComparison.OrdinalIgnoreCase) &&
                r.Value.Equals(value, StringComparison.OrdinalIgnoreCase)))
        {
            NewIisFilterValue = string.Empty;
            return;
        }

        IisFilterRules.Add(new IisFilterRuleItemViewModel(field, value));
        NewIisFilterValue = string.Empty;
    }

    private void RemoveIisFilterRule(IisFilterRuleItemViewModel? rule)
    {
        if (rule is null) return;
        IisFilterRules.Remove(rule);
    }

    private void ClearIisFilterRules()
    {
        if (IisFilterRules.Count == 0) return;
        IisFilterRules.Clear();
    }

    private void AddRuleIfMissing(string field, string value)
    {
        if (IisFilterRules.Any(r =>
                r.Field.Equals(field, StringComparison.OrdinalIgnoreCase) &&
                r.Value.Equals(value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        IisFilterRules.Add(new IisFilterRuleItemViewModel(field, value));
    }

    private void ApplyPresetHealth()
    {
        foreach (var path in new[] { "/health", "/healthz", "/ready", "/readyz", "/live", "/livez", "/ping", "/metrics" })
        {
            AddRuleIfMissing(IisFilterFields.PathPrefix, path);
        }
    }

    private void ApplyPresetOptionsPreflight()
    {
        AddRuleIfMissing(IisFilterFields.Method, "OPTIONS");
    }

    private void ApplyPresetBots()
    {
        foreach (var ua in new[] { "bot", "crawl", "spider", "Googlebot", "bingbot" })
        {
            AddRuleIfMissing(IisFilterFields.UserAgentContains, ua);
        }
    }

    private void ApplyPresetOAuthCallbacks()
    {
        foreach (var path in new[] { "/auth/callback", "/oauth/callback", "/signin-oidc", "/signin-google", "/signin-microsoft" })
        {
            AddRuleIfMissing(IisFilterFields.PathPrefix, path);
        }
    }

    private void ApplyPresetVerySlow()
    {
        // Drop requests slower than 30 seconds — almost always timeouts / hung connections.
        AddRuleIfMissing(IisFilterFields.MaxTimeMs, "30000");
    }

    private void OnEventLogFilterRulesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (EventLogFilterRuleItemViewModel old in e.OldItems)
            {
                old.PropertyChanged -= OnEventLogFilterRuleItemChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (EventLogFilterRuleItemViewModel @new in e.NewItems)
            {
                @new.PropertyChanged += OnEventLogFilterRuleItemChanged;
            }
        }
        PersistDraftIfReady();
        // Mirror IIS behavior: a rule add/remove/clear should auto-apply to the running
        // collector and surface a status-bar notification through ApplyEventLogsAsync.
        if (!_suppressDraftPersistence)
        {
            ScheduleAutoApply(ApplyEventLogsAsync);
        }
    }

    private void OnEventLogFilterRuleItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        PersistDraftIfReady();
        if (!_suppressDraftPersistence)
        {
            ScheduleAutoApply(ApplyEventLogsAsync);
        }
    }

    private void AddEventLogFilterRule()
    {
        var value = NewEventLogFilterValue.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var field = NewEventLogFilterField?.Field ?? EventLogFilterFields.EventId;

        if (EventLogFilterRules.Any(r =>
                r.Field.Equals(field, StringComparison.OrdinalIgnoreCase) &&
                r.Value.Equals(value, StringComparison.OrdinalIgnoreCase)))
        {
            NewEventLogFilterValue = string.Empty;
            return;
        }

        EventLogFilterRules.Add(new EventLogFilterRuleItemViewModel(field, value));
        NewEventLogFilterValue = string.Empty;
    }

    private void RemoveEventLogFilterRule(EventLogFilterRuleItemViewModel? rule)
    {
        if (rule is null) return;
        EventLogFilterRules.Remove(rule);
    }

    private void ClearEventLogFilterRules()
    {
        if (EventLogFilterRules.Count == 0) return;
        EventLogFilterRules.Clear();
    }

    private void AddEventLogRuleIfMissing(string field, string value)
    {
        if (EventLogFilterRules.Any(r =>
                r.Field.Equals(field, StringComparison.OrdinalIgnoreCase) &&
                r.Value.Equals(value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        EventLogFilterRules.Add(new EventLogFilterRuleItemViewModel(field, value));
    }

    private void ApplyEventLogPresetSecurityAudit()
    {
        // Common high-volume Security audit events: logon, logoff, special privileges.
        foreach (var id in new[] { "4624", "4634", "4647", "4672" })
        {
            AddEventLogRuleIfMissing(EventLogFilterFields.EventId, id);
        }
    }

    private void ApplyEventLogPresetServiceState()
    {
        // 7036 = service entered Running/Stopped; 7045 = service installed.
        foreach (var id in new[] { "7036", "7045" })
        {
            AddEventLogRuleIfMissing(EventLogFilterFields.EventId, id);
        }
    }

    private void ApplyEventLogPresetDcom()
    {
        // 10016 = DCOM permission denied (typically benign); 10010 = server timeout.
        foreach (var id in new[] { "10016", "10010" })
        {
            AddEventLogRuleIfMissing(EventLogFilterFields.EventId, id);
        }
    }

    private void AddTag()
    {
        var key = NewTagKey.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var value = NewTagValue.Trim();
        var existing = MetricTags.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            var item = new TagItemViewModel(key, value);
            item.PropertyChanged += OnTagItemPropertyChanged;
            MetricTags.Add(item);
            PersistDraftIfReady();
        }
        else
        {
            existing.Value = value;
        }

        NewTagKey = string.Empty;
        NewTagValue = string.Empty;
    }

    private void RemoveSelectedTag()
    {
        if (SelectedTag != null)
        {
            SelectedTag.PropertyChanged -= OnTagItemPropertyChanged;
            MetricTags.Remove(SelectedTag);
            PersistDraftIfReady();
        }
    }

    private void AddHeartbeatTag()
    {
        var key = NewHeartbeatTagKey.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var value = NewHeartbeatTagValue.Trim();
        var existing = HeartbeatTags.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            var item = new TagItemViewModel(key, value);
            item.PropertyChanged += OnHeartbeatTagItemPropertyChanged;
            HeartbeatTags.Add(item);
            PersistDraftIfReady();
        }
        else
        {
            existing.Value = value;
        }

        NewHeartbeatTagKey = string.Empty;
        NewHeartbeatTagValue = string.Empty;
    }

    private void RemoveSelectedHeartbeatTag()
    {
        if (SelectedHeartbeatTag != null)
        {
            SelectedHeartbeatTag.PropertyChanged -= OnHeartbeatTagItemPropertyChanged;
            HeartbeatTags.Remove(SelectedHeartbeatTag);
            PersistDraftIfReady();
        }
    }

    private List<string> BuildEventLogChannels()
    {
        var channels = new List<string>();
        if (EventLogApplication) channels.Add("Application");
        if (EventLogSystem) channels.Add("System");
        if (EventLogSecurity) channels.Add("Security");
        if (EventLogSetup) channels.Add("Setup");
        channels.AddRange(CustomChannels
            .Select(item => item.Value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value)));

        return channels.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<string> BuildEventLogLevels()
    {
        var levels = new List<string>();
        if (LevelCritical) levels.Add("critical");
        if (LevelError) levels.Add("error");
        if (LevelWarning) levels.Add("warning");
        if (LevelInformation) levels.Add("information");
        if (LevelVerbose) levels.Add("verbose");
        return levels;
    }

    private async Task SendTestLogAsync(string moduleName)
    {
        // Avalonia's default TextBox.Text binding updates the VM on LostFocus, and
        // OnDataSourcesPropertyChanged schedules an 800ms-debounced auto-apply.
        // Clicking Test changes focus (so the VM gets the typed value), but the
        // Test command runs immediately and the debounced auto-apply hasn't yet
        // written the new value into the working config. Without forcing an
        // Apply here, Test snapshots a stale working config and overrides typed
        // moments ago (e.g. Server name override = windows.motivp.com) would
        // silently get ignored — exact symptom observed in the field where the
        // Test payload's host tag fell back to the global ServerName instead of
        // the per-module override.
        //
        // Force-flush by invoking the module's Apply synchronously here.
        // We swallow failures so a transient Apply error never blocks Test
        // diagnosis — the Test will simply use whatever the working config
        // already had.
        try
        {
            Task applyTask = moduleName.ToLowerInvariant() switch
            {
                "eventlog"  => ApplyEventLogsAsync(),
                "iis"       => ApplyIisAsync(),
                "metrics"   => ApplyMetricsAsync(),
                "heartbeat" => ApplyHeartbeatAsync(),
                _           => Task.CompletedTask
            };
            await applyTask;
        }
        catch
        {
            // best-effort flush; never block Test on this
        }

        using var session = _openTestReport?.Invoke(moduleName);
        var result = await _adminClient.SendTestLogAsync(moduleName, session?.Progress);
        _statusCallback(result.Message, result.Success);
    }

    private async Task ToggleModuleAsync(string moduleName, bool enable)
    {
        var result = enable
            ? await _adminClient.EnableModuleAsync(moduleName)
            : await _adminClient.DisableModuleAsync(moduleName);
        _statusCallback(result.Message, result.Success);

        if (result.Success)
        {
            var paused = !enable;
            switch (moduleName)
            {
                case "EventLog": EventLogPaused = paused; break;
                case "IIS": IisPaused = paused; break;
                case "Metrics": MetricsPaused = paused; break;
                case "Heartbeat": HeartbeatPaused = paused; break;
            }
        }
    }

    private static bool IsPresetChannel(string channel)
    {
        return channel.Equals("Application", StringComparison.OrdinalIgnoreCase)
               || channel.Equals("System", StringComparison.OrdinalIgnoreCase)
               || channel.Equals("Security", StringComparison.OrdinalIgnoreCase)
               || channel.Equals("Setup", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshModulePausedStatesAsync()
    {
        var status = await _adminClient.GetStatusAsync();
        var wasConnected = _collectorConnected;
        _collectorConnected = status != null;
        if (wasConnected != _collectorConnected)
        {
            NotifyPropertyChanged(nameof(CollectorConnected));
            NotifyPropertyChanged(nameof(EventLogConfigEditable));
            NotifyPropertyChanged(nameof(IisConfigEditable));
            NotifyPropertyChanged(nameof(MetricsConfigEditable));
            NotifyPropertyChanged(nameof(HeartbeatConfigEditable));
        }

        if (status == null)
        {
            EventLogPaused = false;
            IisPaused = false;
            MetricsPaused = false;
            HeartbeatPaused = false;
            return;
        }

        foreach (var module in status.Modules)
        {
            var paused = !module.Enabled;
            if (module.Name.Equals("EventLog", StringComparison.OrdinalIgnoreCase))
                EventLogPaused = paused;
            else if (module.Name.Equals("IIS", StringComparison.OrdinalIgnoreCase))
                IisPaused = paused;
            else if (module.Name.Equals("Metrics", StringComparison.OrdinalIgnoreCase))
                MetricsPaused = paused;
            else if (module.Name.Equals("Heartbeat", StringComparison.OrdinalIgnoreCase))
                HeartbeatPaused = paused;
        }
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
            return;
        }

        FirewallTabRuntime = string.IsNullOrWhiteSpace(firewallModule.LastError)
            ? $"Runtime: {firewallModule.State}"
            : $"Runtime: {firewallModule.State} ({firewallModule.LastError})";
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
            var diagnostics = (await _adminClient.GetDiagnosticsAsync(500))
                .OrderByDescending(entry => entry.TimestampUtc)
                .ToList();

            RebuildFirewallHistory(diagnostics);
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

    private void RebuildFirewallHistory(IReadOnlyList<DiagnosticEntryDto> diagnostics)
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
            FirewallHistoryRows.Add(new DataSourceFirewallHistoryRow
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
        var compact = Regex.Replace(message, @"\s+", " ").Trim();
        if (compact.Length <= 220)
        {
            return compact;
        }

        return compact[..220] + "...";
    }

    private void OnDataSourcesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressDraftPersistence || string.IsNullOrWhiteSpace(e.PropertyName))
        {
            return;
        }

        if (DraftPersistedPropertyNames.Contains(e.PropertyName))
        {
            PersistDraft();
        }

        if (EventLogAutoApplyProperties.Contains(e.PropertyName))
        {
            ScheduleAutoApply(ApplyEventLogsAsync);
        }
        else if (IisAutoApplyProperties.Contains(e.PropertyName))
        {
            ScheduleAutoApply(ApplyIisAsync);
        }
        else if (MetricsAutoApplyProperties.Contains(e.PropertyName))
        {
            ScheduleAutoApply(ApplyMetricsAsync);
        }
        else if (HeartbeatAutoApplyProperties.Contains(e.PropertyName))
        {
            ScheduleAutoApply(ApplyHeartbeatAsync);
        }
    }

    private void ScheduleAutoApply(Func<Task> applyAction)
    {
        _autoApplyCts?.Cancel();
        _autoApplyCts = new CancellationTokenSource();
        var token = _autoApplyCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(800, token);
                if (!token.IsCancellationRequested)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(applyAction);
                }
            }
            catch (OperationCanceledException)
            {
                // debounced
            }
        }, token);
    }

    private void OnTagItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressDraftPersistence)
        {
            return;
        }

        if (e.PropertyName is nameof(TagItemViewModel.Key) or nameof(TagItemViewModel.Value))
        {
            PersistDraft();
        }
    }

    private void OnHeartbeatTagItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressDraftPersistence)
        {
            return;
        }

        if (e.PropertyName is nameof(TagItemViewModel.Key) or nameof(TagItemViewModel.Value))
        {
            PersistDraft();
        }
    }

    private void PersistDraftIfReady()
    {
        if (_suppressDraftPersistence)
        {
            return;
        }

        PersistDraft();
    }

    private void PersistDraft()
    {
        var draft = new WindowPlacementStore.DataSourcesDraftDto
        {
            EventLogEnabled = EventLogEnabled,
            EventLogPollIntervalSeconds = EventLogPollIntervalSeconds,
            EventLogApplication = EventLogApplication,
            EventLogSystem = EventLogSystem,
            EventLogSecurity = EventLogSecurity,
            EventLogSetup = EventLogSetup,
            LevelCritical = LevelCritical,
            LevelError = LevelError,
            LevelWarning = LevelWarning,
            LevelInformation = LevelInformation,
            LevelVerbose = LevelVerbose,
            EventLogPreviewCount = EventLogPreviewCount,
            EventLogProviderNameOverride = EventLogProviderNameOverride?.Trim() ?? string.Empty,
            EventLogServerNameOverride = EventLogServerNameOverride?.Trim() ?? string.Empty,
            CustomChannels = CustomChannels
                .Select(item => item.Value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            EventLogFilterRules = EventLogFilterRules
                .Where(r => !string.IsNullOrWhiteSpace(r.Value))
                .Select(r => new WindowPlacementStore.EventLogFilterRuleDraftDto
                {
                    Field = r.Field,
                    Value = r.Value.Trim(),
                    Enabled = r.Enabled
                })
                .ToList(),
            IisEnabled = IisEnabled,
            IisPollIntervalSeconds = IisPollIntervalSeconds,
            IisDirectories = IisDirectories
                .Select(item => item.Value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            IisSiteName = IisSiteName.Trim(),
            IisInclude4xx = IisInclude4xx,
            IisInclude5xx = IisInclude5xx,
            IisExcludeStaticFiles = IisExcludeStaticFiles,
            IisPreviewCount = IisPreviewCount,
            IisServerNameOverride = IisServerNameOverride?.Trim() ?? string.Empty,
            IisFilterRules = IisFilterRules
                .Where(r => !string.IsNullOrWhiteSpace(r.Value))
                .Select(r => new WindowPlacementStore.IisFilterRuleDraftDto
                {
                    Field = r.Field,
                    Value = r.Value.Trim(),
                    Enabled = r.Enabled
                })
                .ToList(),
            MetricsEnabled = MetricsEnabled,
            MetricsPollIntervalSeconds = MetricsPollIntervalSeconds,
            MetricsCpu = MetricsCpu,
            MetricsMemory = MetricsMemory,
            MetricsDisk = MetricsDisk,
            MetricsNetwork = MetricsNetwork,
            MetricsServerNameOverride = MetricsServerNameOverride?.Trim() ?? string.Empty,
            MetricTags = MetricTags
                .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                .ToDictionary(
                    item => item.Key.Trim(),
                    item => item.Value.Trim(),
                    StringComparer.OrdinalIgnoreCase),
            HeartbeatEnabled = HeartbeatEnabled,
            HeartbeatPollIntervalSeconds = HeartbeatPollIntervalSeconds,
            HeartbeatMeasurement = HeartbeatMeasurement?.Trim() ?? "heartbeat",
            HeartbeatCollection = HeartbeatCollection?.Trim() ?? "beats",
            HeartbeatIncludeUptime = HeartbeatIncludeUptime,
            HeartbeatIncludeHostnameTag = HeartbeatIncludeHostnameTag,
            HeartbeatIncludeAppVersionTag = HeartbeatIncludeAppVersionTag,
            HeartbeatIncludeCpuPercent = HeartbeatIncludeCpuPercent,
            HeartbeatIncludeMemoryPercent = HeartbeatIncludeMemoryPercent,
            HeartbeatServerNameOverride = HeartbeatServerNameOverride?.Trim() ?? string.Empty,
            HeartbeatEnvironmentOverride = HeartbeatEnvironmentOverride?.Trim() ?? string.Empty,
            HeartbeatTags = HeartbeatTags
                .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                .ToDictionary(
                    item => item.Key.Trim(),
                    item => item.Value.Trim(),
                    StringComparer.OrdinalIgnoreCase),
            UpdatedAtUtc = DateTime.UtcNow
        };

        WindowPlacementStore.SaveDataSourcesDraft(draft);
    }

    private void ApplyDraft(WindowPlacementStore.DataSourcesDraftDto draft)
    {
        EventLogEnabled = draft.EventLogEnabled;
        EventLogPollIntervalSeconds = Math.Max(5, draft.EventLogPollIntervalSeconds);
        EventLogApplication = draft.EventLogApplication;
        EventLogSystem = draft.EventLogSystem;
        EventLogSecurity = draft.EventLogSecurity;
        EventLogSetup = draft.EventLogSetup;
        LevelCritical = draft.LevelCritical;
        LevelError = draft.LevelError;
        LevelWarning = draft.LevelWarning;
        LevelInformation = draft.LevelInformation;
        LevelVerbose = draft.LevelVerbose;
        EventLogPreviewCount = Math.Clamp(draft.EventLogPreviewCount, 1, 50);
        EventLogProviderNameOverride = draft.EventLogProviderNameOverride ?? string.Empty;
        EventLogServerNameOverride = draft.EventLogServerNameOverride ?? string.Empty;

        CustomChannels.Clear();
        foreach (var channel in draft.CustomChannels
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Where(value => !IsPresetChannel(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            CustomChannels.Add(new StringItemViewModel(channel.Trim()));
        }

        EventLogFilterRules.Clear();
        foreach (var rule in draft.EventLogFilterRules ?? Enumerable.Empty<WindowPlacementStore.EventLogFilterRuleDraftDto>())
        {
            if (string.IsNullOrWhiteSpace(rule.Value)) continue;
            var field = string.IsNullOrWhiteSpace(rule.Field) ? EventLogFilterFields.EventId : rule.Field;
            EventLogFilterRules.Add(new EventLogFilterRuleItemViewModel(field, rule.Value.Trim(), rule.Enabled));
        }

        IisEnabled = draft.IisEnabled;
        IisPollIntervalSeconds = Math.Max(5, draft.IisPollIntervalSeconds);
        IisSiteName = draft.IisSiteName ?? string.Empty;
        IisInclude4xx = draft.IisInclude4xx;
        IisInclude5xx = draft.IisInclude5xx;
        IisExcludeStaticFiles = draft.IisExcludeStaticFiles;
        IisPreviewCount = Math.Clamp(draft.IisPreviewCount, 1, 50);
        IisServerNameOverride = draft.IisServerNameOverride ?? string.Empty;

        IisDirectories.Clear();
        foreach (var path in draft.IisDirectories
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IisDirectories.Add(new StringItemViewModel(path.Trim()));
        }

        IisFilterRules.Clear();
        foreach (var rule in draft.IisFilterRules ?? Enumerable.Empty<WindowPlacementStore.IisFilterRuleDraftDto>())
        {
            if (string.IsNullOrWhiteSpace(rule.Value)) continue;
            var field = string.IsNullOrWhiteSpace(rule.Field) ? IisFilterFields.PathPrefix : rule.Field;
            IisFilterRules.Add(new IisFilterRuleItemViewModel(field, rule.Value.Trim(), rule.Enabled));
        }

        MetricsEnabled = draft.MetricsEnabled;
        MetricsPollIntervalSeconds = Math.Max(5, draft.MetricsPollIntervalSeconds);
        MetricsCpu = draft.MetricsCpu;
        MetricsMemory = draft.MetricsMemory;
        MetricsDisk = draft.MetricsDisk;
        MetricsNetwork = draft.MetricsNetwork;
        MetricsServerNameOverride = draft.MetricsServerNameOverride ?? string.Empty;

        foreach (var tag in MetricTags)
        {
            tag.PropertyChanged -= OnTagItemPropertyChanged;
        }

        MetricTags.Clear();
        foreach (var tag in draft.MetricTags.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var item = new TagItemViewModel(tag.Key, tag.Value);
            item.PropertyChanged += OnTagItemPropertyChanged;
            MetricTags.Add(item);
        }

        HeartbeatEnabled = draft.HeartbeatEnabled;
        HeartbeatPollIntervalSeconds = Math.Max(5, draft.HeartbeatPollIntervalSeconds);
        HeartbeatMeasurement = string.IsNullOrWhiteSpace(draft.HeartbeatMeasurement) ? "heartbeat" : draft.HeartbeatMeasurement;
        HeartbeatCollection = string.IsNullOrWhiteSpace(draft.HeartbeatCollection) ? "beats" : draft.HeartbeatCollection;
        HeartbeatIncludeUptime = draft.HeartbeatIncludeUptime;
        HeartbeatIncludeHostnameTag = draft.HeartbeatIncludeHostnameTag;
        HeartbeatIncludeAppVersionTag = draft.HeartbeatIncludeAppVersionTag;
        HeartbeatIncludeCpuPercent = draft.HeartbeatIncludeCpuPercent;
        HeartbeatIncludeMemoryPercent = draft.HeartbeatIncludeMemoryPercent;
        HeartbeatServerNameOverride = draft.HeartbeatServerNameOverride ?? string.Empty;
        HeartbeatEnvironmentOverride = draft.HeartbeatEnvironmentOverride ?? string.Empty;

        foreach (var tag in HeartbeatTags)
        {
            tag.PropertyChanged -= OnHeartbeatTagItemPropertyChanged;
        }

        HeartbeatTags.Clear();
        foreach (var tag in draft.HeartbeatTags.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var item = new TagItemViewModel(tag.Key, tag.Value);
            item.PropertyChanged += OnHeartbeatTagItemPropertyChanged;
            HeartbeatTags.Add(item);
        }
    }
}
