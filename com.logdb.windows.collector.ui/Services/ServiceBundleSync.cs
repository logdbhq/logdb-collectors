namespace com.logdb.windows.collector.ui.Services;

/// <summary>
/// Copies the freshly-swapped service binaries from the Velopack-managed install dir
/// (&lt;install&gt;\current\service\) to wherever SCM has the service binary registered.
///
/// Background: the Velopack package bundles both the UI (at the package root) and the
/// service (in a /service subdirectory). On update, Velopack swaps files inside its
/// own &lt;install&gt;\current\ dir — but if the SCM-registered service binPath points
/// somewhere ELSE on disk (e.g. C:\LogDB.Collector\service\com.logdb.windows.collector.exe,
/// which happens when the service was installed via a separate installer script rather
/// than from the Velopack bundle), Velopack's swap never reaches the actually-running
/// service. The UI updates, the service stays on the old version.
///
/// This class closes that gap: after Velopack has swapped &lt;install&gt;\current\ to the
/// new version and BEFORE the service is restarted, we copy the new
/// &lt;install&gt;\current\service\* over the top of whatever's at the SCM-registered path.
/// If SCM already points at the in-bundle path, this is a no-op.
///
/// Called from <see cref="Program.Main"/>'s OnAfterUpdateFastCallback, which runs
/// elevated in the Velopack updater process after the service has been stopped by
/// OnBeforeUpdateFastCallback. That stop is what makes the file copy safe — without
/// it, the running service holds locks on its own exe/dlls and the copy would fail.
/// </summary>
public static class ServiceBundleSync
{
    /// <summary>
    /// Files we never overwrite at the destination — operator-edited config that
    /// must survive across updates. Matches <see cref="CollectorServiceUpdateService"/>'s
    /// behavior so the two update paths produce identical end-states.
    /// </summary>
    private static readonly HashSet<string> PreservedFiles =
        new(StringComparer.OrdinalIgnoreCase) { "appsettings.json" };

    public static SyncResult SyncFromCurrentBundle(Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows())
            return SyncResult.Skipped("Not running on Windows.");

        var bundleServiceDir = ResolveBundleServiceDirectory();
        if (bundleServiceDir is null || !Directory.Exists(bundleServiceDir))
            return SyncResult.Skipped(
                $"Bundled service directory not found at expected location ({bundleServiceDir ?? "(null)"})." +
                " UI updated, but no in-bundle service binaries to sync.");

        if (!ServiceController.IsInstalled())
            return SyncResult.Skipped(
                $"Service {ServiceController.ServiceName} is not registered; nothing to sync to.");

        var binPath = ServiceController.QueryBinaryPath();
        if (string.IsNullOrWhiteSpace(binPath))
            return SyncResult.Failed(
                $"Could not read BINARY_PATH_NAME for service {ServiceController.ServiceName}." +
                " Service binaries left untouched; restart will run the OLD version.");

        var scmServiceDir = Path.GetDirectoryName(binPath);
        if (string.IsNullOrWhiteSpace(scmServiceDir))
            return SyncResult.Failed($"Could not derive directory from SCM binPath '{binPath}'.");

        if (PathsEqual(scmServiceDir, bundleServiceDir))
            return SyncResult.Skipped(
                $"SCM binPath '{binPath}' is already inside the Velopack bundle — Velopack's swap covered it.");

        if (!Directory.Exists(scmServiceDir))
            return SyncResult.Failed(
                $"SCM-registered service directory '{scmServiceDir}' does not exist." +
                " The service won't start after this update.");

        try
        {
            CopyOverlay(bundleServiceDir, scmServiceDir, log);
            return SyncResult.Synced(
                $"Service binaries synced from '{bundleServiceDir}' to '{scmServiceDir}'.");
        }
        catch (Exception ex)
        {
            return SyncResult.Failed(
                $"Copy failed from '{bundleServiceDir}' to '{scmServiceDir}': {ex.Message}." +
                " Service will restart with the OLD version; operator must reconcile manually.");
        }
    }

    /// <summary>
    /// In a Velopack-managed install, the running exe lives at &lt;install&gt;\current\, so the
    /// bundled service dir is the 'service' sibling next to the running exe. This works
    /// whether the OnAfter callback fires from the UI exe or from Velopack's Update.exe
    /// (in either case ProcessPath is inside the install layout we control).
    /// </summary>
    private static string? ResolveBundleServiceDirectory()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath)) return null;

        var processDir = Path.GetDirectoryName(processPath);
        if (string.IsNullOrWhiteSpace(processDir)) return null;

        // Case 1: running from <install>\current\<ui.exe> — service dir is a sibling.
        var siblingService = Path.Combine(processDir, "service");
        if (Directory.Exists(siblingService)) return siblingService;

        // Case 2: running from <install>\Update.exe — service dir lives one level deeper.
        var nestedService = Path.Combine(processDir, "current", "service");
        if (Directory.Exists(nestedService)) return nestedService;

        return null;
    }

    private static void CopyOverlay(string sourceDir, string destinationDir, Action<string>? log)
    {
        var copied = 0;
        var preserved = 0;

        foreach (var directory in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, directory);
            Directory.CreateDirectory(Path.Combine(destinationDir, relative));
        }

        foreach (var sourceFile in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, sourceFile);
            if (PreservedFiles.Contains(Path.GetFileName(relative)))
            {
                preserved++;
                continue;
            }

            var destinationFile = Path.Combine(destinationDir, relative);
            var targetDirectory = Path.GetDirectoryName(destinationFile);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
                Directory.CreateDirectory(targetDirectory);

            File.Copy(sourceFile, destinationFile, overwrite: true);
            copied++;
        }

        log?.Invoke($"Sync: copied {copied} file(s), preserved {preserved} file(s).");
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public sealed record SyncResult(SyncOutcome Outcome, string Message)
    {
        public static SyncResult Synced(string message) => new(SyncOutcome.Synced, message);
        public static SyncResult Skipped(string message) => new(SyncOutcome.Skipped, message);
        public static SyncResult Failed(string message) => new(SyncOutcome.Failed, message);
    }

    public enum SyncOutcome { Synced, Skipped, Failed }
}
