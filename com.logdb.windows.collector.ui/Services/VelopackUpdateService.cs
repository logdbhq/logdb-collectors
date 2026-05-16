using Velopack;
using Velopack.Sources;

namespace com.logdb.windows.collector.ui.Services;

public sealed class VelopackUpdateService
{
    public const string DefaultUpdateUrl = "https://github.com/vlapec/LogDB.Exporters";

    private readonly string _updateUrl;
    private readonly string? _updateToken;
    private readonly UpdateManager? _updateManager;

    public VelopackUpdateService(string? updateUrl = null)
    {
        _updateUrl = string.IsNullOrWhiteSpace(updateUrl)
            ? Environment.GetEnvironmentVariable("LOGDB_COLLECTOR_UI_UPDATE_URL") ?? DefaultUpdateUrl
            : updateUrl;
        _updateToken = Environment.GetEnvironmentVariable("LOGDB_COLLECTOR_UI_UPDATE_TOKEN");

        try
        {
            _updateManager = new UpdateManager(new GithubSource(_updateUrl, _updateToken, false));
        }
        catch
        {
            _updateManager = null;
        }
    }

    public bool IsConfigured => _updateManager != null;
    public bool IsInstalled => _updateManager?.IsInstalled ?? false;
    public string UpdateUrl => _updateUrl;

    public async Task<(bool Success, string Message)> CheckAndApplyAsync(
        bool silentIfNoUpdate,
        CancellationToken cancellationToken = default)
    {
        if (_updateManager == null)
        {
            return (false, "Velopack update manager is not available.");
        }

        if (!_updateManager.IsInstalled)
        {
            return (false, "App is not running from a Velopack-installed location.");
        }

        try
        {
            var updateInfo = await _updateManager.CheckForUpdatesAsync();
            if (updateInfo == null)
            {
                return silentIfNoUpdate
                    ? (true, "No updates.")
                    : (true, "You are on the latest version.");
            }

            await _updateManager.DownloadUpdatesAsync(updateInfo);
            _updateManager.ApplyUpdatesAndRestart(updateInfo);
            return (true, $"Update {updateInfo.TargetFullRelease.Version} downloaded. Restarting...");
        }
        catch (Exception ex)
        {
            return (false, $"Update check failed: {ex.Message}");
        }
    }
}
