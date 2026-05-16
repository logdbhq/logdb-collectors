using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace com.logdb.windows.collector.ui.Services;

public sealed class ServiceUpdateCheckResult
{
    public bool Success { get; init; }
    public bool UpdateAvailable { get; init; }
    public string Message { get; init; } = string.Empty;
    public string CurrentVersion { get; init; } = "Unknown";
    public string LatestVersion { get; init; } = "-";
    public string ReleaseTag { get; init; } = string.Empty;
    public string AssetName { get; init; } = string.Empty;
    public string AssetDownloadUrl { get; init; } = string.Empty;
}

public sealed class CollectorServiceUpdateService
{
    private const string TagPrefix = "win-col-v";
    private const string DefaultReleasesApiUrl = "https://api.github.com/repos/vlapec/LogDB.Exporters/releases";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly string _releasesApiUrl;
    private readonly string? _updateToken;

    public CollectorServiceUpdateService(HttpClient? httpClient = null, string? releasesApiUrl = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _releasesApiUrl = string.IsNullOrWhiteSpace(releasesApiUrl)
            ? Environment.GetEnvironmentVariable("LOGDB_COLLECTOR_SERVICE_RELEASES_API") ?? DefaultReleasesApiUrl
            : releasesApiUrl;
        _updateToken = Environment.GetEnvironmentVariable("LOGDB_COLLECTOR_SERVICE_UPDATE_TOKEN")
            ?? Environment.GetEnvironmentVariable("LOGDB_COLLECTOR_UI_UPDATE_TOKEN");

        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LogDBCollectorUI", "1.0"));
        }

        if (!string.IsNullOrWhiteSpace(_updateToken) && _httpClient.DefaultRequestHeaders.Authorization == null)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _updateToken);
        }
    }

    public async Task<ServiceUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var service = await ServiceControl.QueryAsync();
        if (!service.Installed)
        {
            return new ServiceUpdateCheckResult
            {
                Success = false,
                Message = "Service is not installed.",
                CurrentVersion = "NotInstalled"
            };
        }

        if (string.IsNullOrWhiteSpace(service.BinaryPath) || !File.Exists(service.BinaryPath))
        {
            return new ServiceUpdateCheckResult
            {
                Success = false,
                Message = "Service binary path could not be resolved.",
                CurrentVersion = service.BinaryVersion
            };
        }

        GitHubReleaseDto? release;
        try
        {
            release = await GetLatestCollectorReleaseAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return new ServiceUpdateCheckResult
            {
                Success = false,
                Message = $"Failed to query collector releases: {ex.Message}",
                CurrentVersion = service.BinaryVersion
            };
        }

        if (release == null)
        {
            return new ServiceUpdateCheckResult
            {
                Success = false,
                Message = "No collector release matching tag pattern win-col-v* was found.",
                CurrentVersion = service.BinaryVersion
            };
        }

        var asset = release.Assets.FirstOrDefault(candidate =>
            candidate.Name.Contains("LogDB.Windows.Collector-", StringComparison.OrdinalIgnoreCase)
            && candidate.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase));

        if (asset == null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
        {
            return new ServiceUpdateCheckResult
            {
                Success = false,
                Message = $"Release {release.TagName} does not contain a collector win-x64 zip asset.",
                CurrentVersion = service.BinaryVersion,
                LatestVersion = ExtractVersionFromTag(release.TagName) ?? release.TagName,
                ReleaseTag = release.TagName
            };
        }

        var latestVersion = ExtractVersionFromTag(release.TagName) ?? release.TagName;
        var updateAvailable = IsUpdateAvailable(service.BinaryVersion, latestVersion);

        return new ServiceUpdateCheckResult
        {
            Success = true,
            CurrentVersion = service.BinaryVersion,
            LatestVersion = latestVersion,
            ReleaseTag = release.TagName,
            AssetName = asset.Name,
            AssetDownloadUrl = asset.BrowserDownloadUrl,
            UpdateAvailable = updateAvailable,
            Message = updateAvailable
                ? $"Update available: {service.BinaryVersion} -> {latestVersion}"
                : $"Service is up to date ({service.BinaryVersion})."
        };
    }

    public async Task<(bool Success, string Message)> ApplyAsync(
        ServiceUpdateCheckResult updateInfo,
        CancellationToken cancellationToken = default)
    {
        if (!updateInfo.Success)
        {
            return (false, "Run update check first and resolve errors.");
        }

        if (!updateInfo.UpdateAvailable)
        {
            return (true, "Service is already up to date.");
        }

        if (!ServiceControl.IsAdministrator())
        {
            return (false, "Updating service requires Administrator privileges.");
        }

        if (string.IsNullOrWhiteSpace(updateInfo.AssetDownloadUrl))
        {
            return (false, "Update asset URL is missing.");
        }

        var service = await ServiceControl.QueryAsync();
        if (!service.Installed)
        {
            return (false, "Service is not installed.");
        }

        if (string.IsNullOrWhiteSpace(service.BinaryPath) || !File.Exists(service.BinaryPath))
        {
            return (false, "Service binary path could not be resolved.");
        }

        var installDirectory = Path.GetDirectoryName(service.BinaryPath);
        if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
        {
            return (false, "Service installation directory was not found.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "logdb-collector-update", Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(tempRoot, "collector.zip");
        var extractPath = Path.Combine(tempRoot, "extract");

        try
        {
            Directory.CreateDirectory(tempRoot);

            using (var response = await _httpClient.GetAsync(updateInfo.AssetDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var destination = File.Create(zipPath);
                await source.CopyToAsync(destination, cancellationToken);
            }

            ZipFile.ExtractToDirectory(zipPath, extractPath, overwriteFiles: true);

            var sourceServiceDirectory = ResolveSourceServiceDirectory(extractPath);
            if (string.IsNullOrWhiteSpace(sourceServiceDirectory))
            {
                return (false, "Downloaded collector package does not contain service payload.");
            }

            var shouldRestart = service.State == ServiceStateKind.Running || service.State == ServiceStateKind.StartPending;
            var stopResult = await ServiceControl.StopAsync();
            if (shouldRestart && !stopResult.Success && !IsNotRunningMessage(stopResult.Message))
            {
                return (false, $"Failed to stop service before update: {stopResult.Message}");
            }

            CopyServicePayload(sourceServiceDirectory, installDirectory);

            if (shouldRestart)
            {
                var startResult = await ServiceControl.StartAsync();
                if (!startResult.Success)
                {
                    return (false, $"Service binaries updated but restart failed: {startResult.Message}");
                }
            }

            return (true, $"Service updated to {updateInfo.LatestVersion}.");
        }
        catch (Exception ex)
        {
            return (false, $"Service update failed: {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // best effort cleanup
            }
        }
    }

    private async Task<GitHubReleaseDto?> GetLatestCollectorReleaseAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(_releasesApiUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var releases = await JsonSerializer.DeserializeAsync<List<GitHubReleaseDto>>(stream, JsonOptions, cancellationToken);
        if (releases == null || releases.Count == 0)
        {
            return null;
        }

        var candidates = releases
            .Where(release => !release.Draft && !release.Prerelease)
            .Where(release => release.TagName.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates
            .OrderByDescending(release => ParseVersionOrDefault(ExtractVersionFromTag(release.TagName)))
            .FirstOrDefault();
    }

    private static string ResolveSourceServiceDirectory(string extractedRoot)
    {
        var expected = Path.Combine(extractedRoot, "service");
        if (Directory.Exists(expected))
        {
            return expected;
        }

        var serviceExe = Directory.GetFiles(extractedRoot, "com.logdb.windows.collector.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(serviceExe)
            ? string.Empty
            : Path.GetDirectoryName(serviceExe) ?? string.Empty;
    }

    private static void CopyServicePayload(string sourceDirectory, string destinationDirectory)
    {
        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relative));
        }

        foreach (var sourceFile in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, sourceFile);
            if (relative.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destinationFile = Path.Combine(destinationDirectory, relative);
            var targetDirectory = Path.GetDirectoryName(destinationFile);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            File.Copy(sourceFile, destinationFile, overwrite: true);
        }
    }

    private static bool IsUpdateAvailable(string currentVersion, string latestVersion)
    {
        var current = ParseVersionOrDefault(currentVersion);
        var latest = ParseVersionOrDefault(latestVersion);

        if (latest == null)
        {
            return false;
        }

        if (current == null)
        {
            return true;
        }

        return latest.CompareTo(current) > 0;
    }

    private static string? ExtractVersionFromTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        if (!tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return tag[TagPrefix.Length..];
    }

    private static Version? ParseVersionOrDefault(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return null;
        }

        var match = Regex.Match(rawVersion, @"\d+\.\d+\.\d+(?:\.\d+)?");
        if (!match.Success)
        {
            return null;
        }

        return Version.TryParse(match.Value, out var version)
            ? version
            : null;
    }

    private static bool IsNotRunningMessage(string message)
    {
        return message.Contains("not been started", StringComparison.OrdinalIgnoreCase)
               || message.Contains("service has not been started", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAssetDto> Assets { get; set; } = new();
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}
