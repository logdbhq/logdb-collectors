using System.Text.Json;
using System.Text.Json.Serialization;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.shared.Services;

namespace com.logdb.windows.collector.Services;

public interface ILogDbServiceUrlResolver
{
    Task<string> ResolveAsync(LogDbConfigDto config, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the gRPC-logger endpoint via the discovery service.
/// Auto-discovers on every call by default (when <see cref="LogDbConfigDto.Endpoint"/>
/// is empty). If the user has set an explicit Endpoint, that wins.
/// Resilient to transient discovery failures: retries with backoff and falls back
/// to the last successfully-resolved endpoint cached on disk.
/// </summary>
public sealed class LogDbServiceUrlResolver : ILogDbServiceUrlResolver
{
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10) };
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LogDbServiceUrlResolver> _logger;
    private readonly string _cachePath;

    public LogDbServiceUrlResolver(
        IHttpClientFactory httpClientFactory,
        ILogger<LogDbServiceUrlResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cachePath = CollectorPathDefaults.EndpointCachePath;
    }

    public async Task<string> ResolveAsync(LogDbConfigDto config, CancellationToken cancellationToken = default)
    {
        // Explicit Endpoint override wins — no discovery call.
        if (!string.IsNullOrWhiteSpace(config.Endpoint))
        {
            DebugLog($"Using explicit Endpoint override (no discovery call): {config.Endpoint}");
            return config.Endpoint;
        }

        if (string.IsNullOrWhiteSpace(config.DiscoveryUrl))
        {
            throw new InvalidOperationException("LogDB endpoint is not configured and discovery URL is empty.");
        }

        DebugLog($"Resolving via discovery: {config.DiscoveryUrl} | apiKey={Mask(config.ApiKey)}");

        // Retry with exponential-ish backoff (1s, 3s, 10s).
        Exception? lastError = null;
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            try
            {
                var resolved = await CallDiscoveryAsync(config, cancellationToken);
                SaveCache(config.ApiKey, config.DiscoveryUrl, resolved);
                DebugLog($"Discovery OK on attempt {attempt + 1}: {resolved} (cached)");
                if (attempt > 0)
                {
                    _logger.LogInformation("Discovery succeeded on retry {Attempt} ({Endpoint})", attempt, resolved);
                }
                return resolved;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                DebugLog($"Discovery attempt {attempt + 1}/{RetryDelays.Length + 1} FAILED: {ex.GetType().Name}: {ex.Message}");
                _logger.LogWarning(
                    "Discovery attempt {Attempt}/{Total} failed: {Message}",
                    attempt + 1, RetryDelays.Length + 1, ex.Message);

                if (attempt < RetryDelays.Length)
                {
                    DebugLog($"Backing off {RetryDelays[attempt].TotalSeconds}s before retry…");
                    await Task.Delay(RetryDelays[attempt], cancellationToken);
                }
            }
        }

        // All retries exhausted — fall back to cached value if we have one.
        var cached = LoadCache(config.ApiKey, config.DiscoveryUrl);
        if (!string.IsNullOrWhiteSpace(cached?.Endpoint))
        {
            var ageMin = (int)(DateTime.UtcNow - cached.ResolvedAtUtc).TotalMinutes;
            DebugLog($"Discovery exhausted; serving CACHED endpoint {cached.Endpoint} (age {ageMin} min)");
            _logger.LogWarning(
                "Discovery unreachable after {Retries} retries — using cached endpoint {Endpoint} (resolved {AgeMinutes} min ago).",
                RetryDelays.Length + 1,
                cached.Endpoint,
                ageMin);
            return cached.Endpoint;
        }

        // No cache, all attempts failed — give up with a clear message.
        var detail = lastError is null ? "unknown reason" : $"{lastError.GetType().Name}: {lastError.Message}";
        DebugLog($"Discovery exhausted AND no cache — giving up. Last error: {detail}");
        throw new InvalidOperationException(
            $"Discovery service at {config.DiscoveryUrl} is unreachable and no cached endpoint is available. " +
            $"Last error: {detail}",
            lastError);
    }

    private static string Mask(string? apiKey)
    {
        return string.IsNullOrEmpty(apiKey) ? "(none)" : "(configured)";
    }

    private static void DebugLog(string message)
    {
        var line = $"[LogDBResolver {DateTime.Now:HH:mm:ss.fff}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        Console.WriteLine(line);
    }

    private async Task<string> CallDiscoveryAsync(LogDbConfigDto config, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(LogDbServiceUrlResolver));
        client.Timeout = TimeSpan.FromSeconds(10);

        using var request = new HttpRequestMessage(HttpMethod.Get, config.DiscoveryUrl);
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("X-API-Key", config.ApiKey);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Discovery returned HTTP {(int)response.StatusCode} {response.ReasonPhrase} for {config.DiscoveryUrl}.");
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Discovery returned an empty body.");
        }

        // /resolve/{serviceId} returns { serviceUrl, accountId, ... }
        try
        {
            var resolved = JsonSerializer.Deserialize<DiscoveryResolvedService>(text);
            if (!string.IsNullOrWhiteSpace(resolved?.ServiceUrl))
            {
                return resolved.ServiceUrl.Trim();
            }
        }
        catch
        {
            // Not a ResolvedService — fall through.
        }

        // /get/{serviceId} returns just a JSON string with the URL.
        try
        {
            var fromJson = JsonSerializer.Deserialize<string>(text);
            if (!string.IsNullOrWhiteSpace(fromJson))
            {
                return fromJson.Trim();
            }
        }
        catch
        {
            // Not a JSON string — fall through.
        }

        // Plain text response (some discovery deployments return this).
        return text.Trim().Trim('"');
    }

    private void SaveCache(string? apiKey, string discoveryUrl, string endpoint)
    {
        try
        {
            var entry = new CachedEndpoint
            {
                ApiKeyFingerprint = Fingerprint(apiKey),
                DiscoveryUrl = discoveryUrl,
                Endpoint = endpoint,
                ResolvedAtUtc = DateTime.UtcNow
            };
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(entry, JsonOpts));
            DebugLog($"Wrote cache: {_cachePath} → {endpoint}");
        }
        catch (Exception ex)
        {
            DebugLog($"SaveCache FAILED ({_cachePath}): {ex.GetType().Name}: {ex.Message}");
            _logger.LogDebug(ex, "Failed to persist endpoint cache to {Path} (non-fatal).", _cachePath);
        }
    }

    private CachedEndpoint? LoadCache(string? apiKey, string discoveryUrl)
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                DebugLog($"LoadCache: file not present at {_cachePath}");
                return null;
            }

            var entry = JsonSerializer.Deserialize<CachedEndpoint>(File.ReadAllText(_cachePath));
            if (entry == null)
            {
                DebugLog($"LoadCache: file present but deserialized to null");
                return null;
            }

            // Only honor cache when it was written for the same API key + discovery URL.
            // (API key change or pointing at a different discovery server invalidates it.)
            if (!string.Equals(entry.ApiKeyFingerprint, Fingerprint(apiKey), StringComparison.Ordinal))
            {
                DebugLog("LoadCache: apiKey fingerprint mismatch — cache invalid for current key");
                return null;
            }

            if (!string.Equals(entry.DiscoveryUrl, discoveryUrl, StringComparison.OrdinalIgnoreCase))
            {
                DebugLog($"LoadCache: discoveryUrl mismatch (cached={entry.DiscoveryUrl}, current={discoveryUrl})");
                return null;
            }

            DebugLog($"LoadCache: hit — {entry.Endpoint} (resolved {entry.ResolvedAtUtc:O})");
            return entry;
        }
        catch (Exception ex)
        {
            DebugLog($"LoadCache FAILED ({_cachePath}): {ex.GetType().Name}: {ex.Message}");
            _logger.LogDebug(ex, "Failed to read endpoint cache from {Path} (non-fatal).", _cachePath);
            return null;
        }
    }

    private static string Fingerprint(string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return string.Empty;
        // Don't store the API key itself in the cache — fingerprint via simple hash so we
        // can detect key changes without exposing the key on disk.
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(bytes);
    }

    private sealed class DiscoveryResolvedService
    {
        [JsonPropertyName("serviceUrl")]
        public string? ServiceUrl { get; set; }
    }

    private sealed class CachedEndpoint
    {
        public string ApiKeyFingerprint { get; set; } = string.Empty;
        public string DiscoveryUrl { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public DateTime ResolvedAtUtc { get; set; }
    }
}
