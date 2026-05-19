using System.Text.Json;

namespace com.logdb.windows.collector.ui.Services;

public static class WindowPlacementStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LogDB",
            "collector-ui",
            "user-settings.json");

    public static MainWindowPlacementDto? LoadMainWindowPlacement()
    {
        try
        {
            var settings = LoadSettings();
            if (settings?.MainWindow == null)
            {
                return null;
            }

            if (settings.MainWindow.Width <= 0 || settings.MainWindow.Height <= 0)
            {
                return null;
            }

            return settings.MainWindow;
        }
        catch
        {
            return null;
        }
    }

    public static DataSourcesDraftDto? LoadDataSourcesDraft()
    {
        try
        {
            return LoadSettings()?.DataSourcesDraft;
        }
        catch
        {
            return null;
        }
    }

    public static bool LoadIsDarkTheme(bool defaultValue = true)
    {
        try
        {
            return LoadSettings()?.IsDarkTheme ?? defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    public static void SaveIsDarkTheme(bool isDark)
    {
        var settings = LoadSettings() ?? new UserSettingsDto();
        settings.IsDarkTheme = isDark;
        SaveSettings(settings);
    }

    public static DiagnosticsOnlineColumnsDto? LoadDiagnosticsOnlineColumns()
    {
        try
        {
            return LoadSettings()?.DiagnosticsOnlineColumns;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveMainWindowPlacement(MainWindowPlacementDto placement)
    {
        var settings = LoadSettings() ?? new UserSettingsDto();
        settings.MainWindow = placement;
        SaveSettings(settings);
    }

    public static void SaveDataSourcesDraft(DataSourcesDraftDto draft)
    {
        var settings = LoadSettings() ?? new UserSettingsDto();
        settings.DataSourcesDraft = draft;
        SaveSettings(settings);
    }

    public static void SaveDiagnosticsOnlineColumns(DiagnosticsOnlineColumnsDto columns)
    {
        var settings = LoadSettings() ?? new UserSettingsDto();
        settings.DiagnosticsOnlineColumns = columns;
        SaveSettings(settings);
    }

    private static UserSettingsDto? LoadSettings()
    {
        if (!File.Exists(SettingsPath))
        {
            return null;
        }

        var json = File.ReadAllText(SettingsPath);
        return JsonSerializer.Deserialize<UserSettingsDto>(json, JsonOptions);
    }

    private static void SaveSettings(UserSettingsDto settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var directory = Path.GetDirectoryName(SettingsPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var tempPath = SettingsPath + ".tmp";
        File.WriteAllText(tempPath, json);

        if (File.Exists(SettingsPath))
        {
            File.Replace(tempPath, SettingsPath, null);
        }
        else
        {
            File.Move(tempPath, SettingsPath);
        }
    }

    public sealed class UserSettingsDto
    {
        public MainWindowPlacementDto MainWindow { get; set; } = new();
        public DataSourcesDraftDto? DataSourcesDraft { get; set; }
        public DiagnosticsOnlineColumnsDto? DiagnosticsOnlineColumns { get; set; }
        public bool? IsDarkTheme { get; set; }
    }

    public sealed class MainWindowPlacementDto
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public string WindowState { get; set; } = "Normal";
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class DataSourcesDraftDto
    {
        public bool EventLogEnabled { get; set; }
        public int EventLogPollIntervalSeconds { get; set; } = 60;
        public bool EventLogApplication { get; set; } = true;
        public bool EventLogSystem { get; set; } = true;
        public bool EventLogSecurity { get; set; }
        public bool EventLogSetup { get; set; }
        public bool LevelCritical { get; set; } = true;
        public bool LevelError { get; set; } = true;
        public bool LevelWarning { get; set; } = true;
        public bool LevelInformation { get; set; } = true;
        public bool LevelVerbose { get; set; }
        public int EventLogPreviewCount { get; set; } = 20;
        public List<string> CustomChannels { get; set; } = new();
        public List<EventLogFilterRuleDraftDto> EventLogFilterRules { get; set; } = new();
        public string EventLogProviderNameOverride { get; set; } = string.Empty;

        public bool IisEnabled { get; set; }
        public int IisPollIntervalSeconds { get; set; } = 60;
        public List<string> IisDirectories { get; set; } = new();
        public string IisSiteName { get; set; } = string.Empty;
        public bool IisInclude4xx { get; set; } = true;
        public bool IisInclude5xx { get; set; } = true;
        public bool IisExcludeStaticFiles { get; set; }
        public int IisPreviewCount { get; set; } = 20;
        public List<IisFilterRuleDraftDto> IisFilterRules { get; set; } = new();

        public bool MetricsEnabled { get; set; } = true;
        public int MetricsPollIntervalSeconds { get; set; } = 60;
        public bool MetricsCpu { get; set; } = true;
        public bool MetricsMemory { get; set; } = true;
        public bool MetricsDisk { get; set; } = true;
        public bool MetricsNetwork { get; set; } = true;
        public string MetricsServerNameOverride { get; set; } = string.Empty;
        public Dictionary<string, string> MetricTags { get; set; } = new();

        public bool HeartbeatEnabled { get; set; }
        public int HeartbeatPollIntervalSeconds { get; set; } = 60;
        public string HeartbeatMeasurement { get; set; } = "heartbeat";
        public string HeartbeatCollection { get; set; } = "beats";
        public bool HeartbeatIncludeUptime { get; set; } = true;
        public bool HeartbeatIncludeHostnameTag { get; set; } = true;
        public bool HeartbeatIncludeAppVersionTag { get; set; }
        public bool HeartbeatIncludeCpuPercent { get; set; }
        public bool HeartbeatIncludeMemoryPercent { get; set; }
        public string HeartbeatServerNameOverride { get; set; } = string.Empty;
        public string HeartbeatEnvironmentOverride { get; set; } = string.Empty;
        public Dictionary<string, string> HeartbeatTags { get; set; } = new();

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class IisFilterRuleDraftDto
    {
        public string Field { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
    }

    public sealed class EventLogFilterRuleDraftDto
    {
        public string Field { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
    }

    public sealed class DiagnosticsOnlineColumnsDto
    {
        public double Time { get; set; } = 170;
        public double Level { get; set; } = 90;
        public double Category { get; set; } = 240;
        public double Message { get; set; } = 640;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
