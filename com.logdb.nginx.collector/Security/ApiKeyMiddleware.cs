using System.Security.Cryptography;
using System.Text;

namespace com.logdb.nginx.collector.Security;

/// <summary>
/// Guards the collector API with a shared key supplied in the <c>X-Api-Key</c> header.
/// Health endpoints stay open for orchestrator probes. When no key is configured the
/// middleware is a no-op (backward compatible) and a warning is logged at startup.
/// </summary>
public sealed class ApiKeyMiddleware
{
    public const string HeaderName = "X-Api-Key";

    private readonly RequestDelegate _next;
    private readonly byte[]? _expectedKey;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        var key = configuration["Auth:ApiKey"];
        _expectedKey = string.IsNullOrEmpty(key) ? null : Encoding.UTF8.GetBytes(key);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Auth disabled when no key configured.
        if (_expectedKey is null)
        {
            await _next(context);
            return;
        }

        // Health/readiness probes must stay reachable without a key.
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var provided)
            || !IsValid(provided.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or missing API key" });
            return;
        }

        await _next(context);
    }

    private bool IsValid(string provided)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        // FixedTimeEquals returns false for length mismatches without leaking timing.
        return CryptographicOperations.FixedTimeEquals(providedBytes, _expectedKey);
    }
}

public static class ApiKeyMiddlewareExtensions
{
    public static IApplicationBuilder UseApiKeyAuth(this IApplicationBuilder app)
        => app.UseMiddleware<ApiKeyMiddleware>();
}
