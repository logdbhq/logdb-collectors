using System.Diagnostics;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace com.logdb.windows.collector.ui.Services;

/// <summary>
/// Wraps Velopack's <see cref="UpdateManager"/> against the logdbhq GitHub Releases
/// feed. Responsibilities:
///
/// 1. Periodically check for newer versions (called by Program at startup, in background).
/// 2. Download a pending update.
/// 3. Apply + restart, hand-in-hand with <see cref="ServiceController"/> stop/start
///    around the file-swap (wired via Velopack's OnBefore/OnAfterUpdateFastCallback in
///    Program.Main — those hooks fire from a child updater process during the apply phase).
///
/// Because the service exe lives inside the Velopack-managed install directory (alongside
/// the UI exe — see package-collector-ui-velopack.ps1), the service must be stopped
/// before Velopack can replace its files. The Apply flow runs ELEVATED (re-launched
/// via Program with --apply-update + runas verb) so sc.exe stop/start succeed.
///
/// Feed: GitHub Releases on https://github.com/logdbhq/logdb-collectors. Velopack reads
/// the latest release asset matching its release index. The release.yml workflow already
/// uploads ./releases/collector-ui-velopack/* on every win-col-v* tag.
/// </summary>
public sealed class UpdaterService
{
    private const string GithubRepoUrl = "https://github.com/logdbhq/logdb-collectors";

    private readonly UpdateManager _manager;

    public UpdaterService()
    {
        // GithubSource(repoUrl, accessToken, preReleases). Anonymous GitHub API
        // access is limited to 60 requests/hour; operators can set
        // LOGDB_COLLECTOR_UI_UPDATE_TOKEN (a PAT with public-repo read) to lift
        // that to 5000/hour. Same env var the VelopackUpdateService honours.
        var token = Environment.GetEnvironmentVariable("LOGDB_COLLECTOR_UI_UPDATE_TOKEN");
        _manager = new UpdateManager(new GithubSource(GithubRepoUrl, token, false));
    }

    public bool IsInstalledByVelopack => _manager.IsInstalled;

    public string? CurrentVersion =>
        _manager.CurrentVersion?.ToString()
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString();

    /// <summary>
    /// Returns the newer version Velopack would install, or null if up-to-date / not
    /// running inside a Velopack-managed install (e.g. running from a dev build's bin
    /// folder, where there's no install metadata to compare against).
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        var (info, _) = await CheckWithErrorAsync(cancellationToken).ConfigureAwait(false);
        return info;
    }

    /// <summary>
    /// Like <see cref="CheckAsync"/> but surfaces the failure reason instead of
    /// swallowing it — callers need to tell "up to date" apart from "GitHub
    /// rate-limited the check" (the latter must back off, not retry-loop).
    /// </summary>
    public async Task<(UpdateInfo? Info, string? Error)> CheckWithErrorAsync(CancellationToken cancellationToken = default)
    {
        if (!_manager.IsInstalled) return (null, null);
        try
        {
            return (await _manager.CheckForUpdatesAsync().ConfigureAwait(false), null);
        }
        catch (Exception ex)
        {
            // Network down, GitHub rate-limit, no release yet on the feed — none of
            // those should crash the UI. Caller decides whether to back off.
            return (null, ex.Message);
        }
    }

    public async Task DownloadAsync(UpdateInfo info, CancellationToken cancellationToken = default)
    {
        await _manager.DownloadUpdatesAsync(info).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the staged update and restarts the current exe. Velopack's
    /// OnBeforeUpdateFastCallback (wired in Program.Main) fires inside the updater
    /// child-process and stops the service before file swap; OnAfterUpdateFastCallback
    /// fires after the swap and restarts the service. Caller MUST be elevated, otherwise
    /// the sc.exe calls in those hooks fail silently.
    /// </summary>
    public void ApplyAndRestart(UpdateInfo info)
    {
        _manager.ApplyUpdatesAndRestart(info);
    }

    /// <summary>
    /// Re-launches the current exe with the <see cref="Program.ApplyUpdateArg"/> CLI
    /// flag using the "runas" shell verb — Windows shows a UAC prompt, the elevated
    /// instance picks up the flag, re-runs the check/download/apply flow as admin.
    /// Returns false if the user dismissed the UAC prompt or the spawn failed.
    /// </summary>
    public static bool RelaunchElevatedToApplyUpdate()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = Program.ApplyUpdateArg,
                UseShellExecute = true, // required for verb=runas (UAC)
                Verb = "runas"
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            // User clicked No on UAC, or shell-execute failed.
            return false;
        }
    }
}
