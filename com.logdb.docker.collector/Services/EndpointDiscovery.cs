using System.Text.Json;

namespace com.logdb.docker.collector.Services;

public static class EndpointDiscovery
{
    private const string DiscoveryUrl = "https://discovery.logdb.site/resolve/grpc-logger";

    public static bool IsPlaceholder(string? endpoint) =>
        string.IsNullOrWhiteSpace(endpoint)
        || endpoint.Contains("your-service.com", StringComparison.OrdinalIgnoreCase)
        || endpoint.Contains("localhost", StringComparison.OrdinalIgnoreCase);

    public static async Task<string?> DiscoverGrpcLoggerUrlAsync(
        string? apiKey,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        try
        {
            log?.Invoke($"Discovering LogDB gRPC endpoint (api-key: {(string.IsNullOrWhiteSpace(apiKey) ? "missing" : "present")})...");
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new HttpRequestMessage(HttpMethod.Get, DiscoveryUrl);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.TryAddWithoutValidation("X-API-Key", apiKey);

            using var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                log?.Invoke($"Discovery service returned {(int)response.StatusCode} {response.ReasonPhrase}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("serviceUrl", out var prop))
            {
                var url = prop.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    log?.Invoke($"Discovered LogDB gRPC endpoint: {url}");
                    return url;
                }
            }

            log?.Invoke("Discovery response did not contain serviceUrl");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Discovery service failed: {ex.Message}");
        }
        return null;
    }
}
