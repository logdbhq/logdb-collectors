using com.logdb.windows.collector.shared.Contracts;
using LogDB.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace com.logdb.windows.collector.Services;

internal static class LogDbClientFactory
{
    public static ILogDBClient Create(
        LogDbConfigDto config,
        string serviceUrl,
        ILoggerFactory loggerFactory,
        string? defaultApplication = null,
        string? defaultEnvironment = null)
    {
        var protocol = Enum.TryParse<LogDBProtocol>(config.Protocol, ignoreCase: true, out var parsedProtocol)
            ? parsedProtocol
            : LogDBProtocol.Native;

        var options = new LogDBLoggerOptions
        {
            ApiKey = config.ApiKey,
            ServiceUrl = serviceUrl,
            Protocol = protocol,
            EnableBatching = config.Batch.EnableBatching,
            BatchSize = config.Batch.BatchSize,
            FlushInterval = TimeSpan.FromSeconds(Math.Max(1, config.Batch.FlushIntervalSeconds)),
            // HARD-PIN to false. Compressed RPCs silently drop on at least
            // some backends — see UiLogDbClientFactory for full rationale.
            EnableCompression = false,
            MaxRetries = Math.Max(0, config.Retry.MaxRetries),
            EnableCircuitBreaker = config.Retry.EnableCircuitBreaker
        };

        // LogBeat.Environment / LogBeat.Application are tag-wrappers (not
        // typed proto fields) per LogDB.Client 5.1.1 README. The SDK uses
        // DefaultEnvironment / DefaultApplication as the wire-level baseline
        // and defaults DefaultEnvironment to "production" when unset. Pinning
        // these matches the per-record tag value the exporters set, so the
        // server-side log_beats / log_events columns resolve to the value the
        // user actually configured instead of "production".
        if (!string.IsNullOrWhiteSpace(defaultApplication))
        {
            options.DefaultApplication = defaultApplication;
        }
        if (!string.IsNullOrWhiteSpace(defaultEnvironment))
        {
            options.DefaultEnvironment = defaultEnvironment;
        }

        return new LogDBClient(Options.Create(options), loggerFactory.CreateLogger<LogDBClient>());
    }
}
