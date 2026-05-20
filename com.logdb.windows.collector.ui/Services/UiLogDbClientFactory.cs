using com.logdb.windows.collector.shared.Contracts;
using LogDB.Extensions.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace com.logdb.windows.collector.ui.Services;

internal static class UiLogDbClientFactory
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
            // HARD-PIN to false — the compressed RPCs (SendCompressedLog /
            // SendCompressedLogBeat) silently drop rows on at least some
            // backends, so always go via Log / LogBeat regardless of what the
            // DTO or on-disk appsettings.json says.
            EnableCompression = false,
            MaxRetries = Math.Max(0, config.Retry.MaxRetries),
            EnableCircuitBreaker = config.Retry.EnableCircuitBreaker
        };

        // LogBeat.Environment and LogBeat.Application are tag-wrappers, not
        // typed proto fields (per LogDB.Client 5.1.1 README) — but the SDK also
        // pulls from LogDBLoggerOptions.DefaultEnvironment / DefaultApplication
        // as the wire-level baseline when the per-record tag-wrapper is empty,
        // and the SDK defaults DefaultEnvironment to "production" when nothing
        // is set. So pin the options to the same value we set on the per-record
        // beat — guarantees the server-side log_beats.environment column
        // resolves to our intended value rather than the SDK's "production"
        // fallback.
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
