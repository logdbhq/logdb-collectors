using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using com.logdb.windows.collector.shared.Contracts;

namespace com.logdb.windows.collector.Services;

public interface ILogDbServiceUrlResolver
{
    Task<string> ResolveAsync(LogDbConfigDto config, CancellationToken cancellationToken = default);
}

public sealed class LogDbServiceUrlResolver : ILogDbServiceUrlResolver
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LogDbServiceUrlResolver> _logger;

    public LogDbServiceUrlResolver(
        IHttpClientFactory httpClientFactory,
        ILogger<LogDbServiceUrlResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> ResolveAsync(LogDbConfigDto config, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(config.Endpoint))
        {
            return config.Endpoint;
        }

        if (string.IsNullOrWhiteSpace(config.DiscoveryUrl))
        {
            throw new InvalidOperationException("LogDB endpoint is not configured and discovery URL is empty.");
        }

        var client = _httpClientFactory.CreateClient(nameof(LogDbServiceUrlResolver));
        client.Timeout = TimeSpan.FromSeconds(10);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, config.DiscoveryUrl);
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
            {
                request.Headers.TryAddWithoutValidation("X-API-Key", config.ApiKey);
            }

            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("Discovery returned an empty LogDB endpoint.");
            }

            // Try parsing as ResolvedService JSON (from /resolve endpoint)
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
                // Not a ResolvedService response - fall through
            }

            // Try parsing as a plain JSON string (from /get endpoint, backward compat)
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
                // Not a JSON string - fall through
            }

            // Plain text response
            return text.Trim().Trim('"');
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover LogDB endpoint from {DiscoveryUrl}", config.DiscoveryUrl);
            throw new InvalidOperationException(
                $"Failed to discover LogDB endpoint from {config.DiscoveryUrl}.", ex);
        }
    }

    private sealed class DiscoveryResolvedService
    {
        [JsonPropertyName("serviceUrl")]
        public string? ServiceUrl { get; set; }
    }
}
