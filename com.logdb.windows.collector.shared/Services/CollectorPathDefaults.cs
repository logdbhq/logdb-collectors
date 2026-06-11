namespace com.logdb.windows.collector.shared.Services;

public static class CollectorPathDefaults
{
    public const string BaseDirectoryEnvironmentVariable = "LOGDB_COLLECTOR_BASE_DIR";

    public static string BaseDirectory =>
        Environment.GetEnvironmentVariable(BaseDirectoryEnvironmentVariable)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LogDB", "collector");

    public static string ConfigPath => Path.Combine(BaseDirectory, "appsettings.json");
    public static string UiSettingsPath => Path.Combine(BaseDirectory, "ui-settings.json");
    public static string LogDirectory => Path.Combine(BaseDirectory, "logs");
    public static string EndpointCachePath => Path.Combine(BaseDirectory, "endpoint-cache.json");
    public static string FailureLogPath => Path.Combine(BaseDirectory, "failures.json");
    public static string SendActivityPath => Path.Combine(BaseDirectory, "send-activity.json");
}
