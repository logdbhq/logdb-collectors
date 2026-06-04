namespace com.logdb.windows.collector.ui.Services;

/// <summary>
/// Tee-writes Velopack hook output to both console AND a persistent log file at
/// %ProgramData%\LogDB\collector\update.log. The console writes from the hooks
/// would otherwise vanish — they happen inside a Velopack-spawned child updater
/// process that exits before anyone can read its stdout. With the file mirror,
/// post-mortem on a "UI updated but service stayed old" report is possible by
/// just reading that file.
///
/// Best-effort: if the file write fails (locked directory, no admin on a path
/// that needs it, disk full), the console write still happens and the hook
/// keeps going. Never throws.
/// </summary>
public static class UpdateLog
{
    private static readonly Lazy<string?> LogPath = new(ResolveLogPath);

    public static void Write(string message)
    {
        Console.WriteLine(message);
        AppendToFile(message);
    }

    private static void AppendToFile(string message)
    {
        var path = LogPath.Value;
        if (path is null) return;

        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{System.Environment.NewLine}";
            File.AppendAllText(path, line);
        }
        catch
        {
            // Best-effort; never crash the Velopack hook over a log write.
        }
    }

    private static string? ResolveLogPath()
    {
        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(programData)) return null;
            var dir = Path.Combine(programData, "LogDB", "collector");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "update.log");
        }
        catch
        {
            return null;
        }
    }
}
