using com.logdb.windows.collector.shared.Contracts;
using Microsoft.Extensions.Options;

namespace com.logdb.windows.collector.Services;

/// <summary>
/// Single source of truth for the resolved gRPC endpoint within the collector
/// service process. Resolves discovery exactly ONCE on first access (via the
/// underlying <see cref="ILogDbServiceUrlResolver"/>) and caches the result for
/// the rest of the process lifetime — until invalidated.
///
/// Every running module (EventLog, IIS, Metrics, Heartbeat, …) and every UI
/// request via the control channel asks this store for the endpoint. They are
/// guaranteed to read the same value at any moment, eliminating the class of
/// bugs where module-A and module-B independently resolve discovery and land
/// on different endpoints when the discovery server is non-deterministic.
///
/// The store is invalidated automatically when LogDB.ApiKey or
/// LogDB.DiscoveryUrl change (those are the only inputs that can legitimately
/// change the resolved value), so the next access re-resolves freshly.
/// </summary>
public interface IRuntimeEndpointStore
{
    /// <summary>
    /// Returns the locked-in endpoint, resolving on first call. Subsequent
    /// calls return the cached value until <see cref="Invalidate"/> is fired
    /// (e.g. because the API key changed).
    /// </summary>
    Task<string> GetEndpointAsync(CancellationToken cancellationToken = default);

    /// <summary>Synchronous peek — null if not yet resolved.</summary>
    string? CurrentEndpoint { get; }

    /// <summary>Drop the cached value. Next GetEndpointAsync re-resolves.</summary>
    void Invalidate();
}

public sealed class RuntimeEndpointStore : IRuntimeEndpointStore, IDisposable
{
    private readonly ILogDbServiceUrlResolver _resolver;
    private readonly IOptionsMonitor<CollectorConfigDto> _configMonitor;
    private readonly ILogger<RuntimeEndpointStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDisposable? _configChangeSubscription;
    private string? _endpoint;
    private string? _lastApiKey;
    private string? _lastDiscoveryUrl;

    public RuntimeEndpointStore(
        ILogDbServiceUrlResolver resolver,
        IOptionsMonitor<CollectorConfigDto> configMonitor,
        ILogger<RuntimeEndpointStore> logger)
    {
        _resolver = resolver;
        _configMonitor = configMonitor;
        _logger = logger;
        _configChangeSubscription = _configMonitor.OnChange(OnConfigChanged);
    }

    public string? CurrentEndpoint => _endpoint;

    public async Task<string> GetEndpointAsync(CancellationToken cancellationToken = default)
    {
        if (_endpoint is { Length: > 0 })
        {
            return _endpoint;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_endpoint is { Length: > 0 })
            {
                return _endpoint;
            }

            var config = _configMonitor.CurrentValue;
            var resolved = await _resolver.ResolveAsync(config.LogDB, cancellationToken).ConfigureAwait(false);
            _endpoint = resolved;
            _lastApiKey = config.LogDB.ApiKey;
            _lastDiscoveryUrl = config.LogDB.DiscoveryUrl;
            _logger.LogInformation(
                "Runtime endpoint locked: {Endpoint}. Every collector module + control-channel reply will use this value until the API key or DiscoveryUrl changes.",
                resolved);
            return resolved;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate()
    {
        var previous = _endpoint;
        _endpoint = null;
        if (!string.IsNullOrEmpty(previous))
        {
            _logger.LogInformation("Runtime endpoint invalidated (was {Previous}); next access will re-resolve.", previous);
        }
    }

    private void OnConfigChanged(CollectorConfigDto newConfig, string? _)
    {
        // Only inputs that affect endpoint resolution warrant a refresh.
        // Ignore everything else (module toggles, tag changes, etc.) so we
        // don't bounce the gRPC channel on every Apply.
        var apiKeyChanged = !string.Equals(newConfig.LogDB.ApiKey, _lastApiKey, StringComparison.Ordinal);
        var discoveryUrlChanged = !string.Equals(newConfig.LogDB.DiscoveryUrl, _lastDiscoveryUrl, StringComparison.Ordinal);
        if (apiKeyChanged || discoveryUrlChanged)
        {
            _logger.LogInformation(
                "Config change affects endpoint resolution (apiKeyChanged={ApiKeyChanged}, discoveryUrlChanged={DiscoveryUrlChanged}); invalidating runtime endpoint.",
                apiKeyChanged, discoveryUrlChanged);
            Invalidate();
        }
    }

    public void Dispose()
    {
        _configChangeSubscription?.Dispose();
        _gate.Dispose();
    }
}
