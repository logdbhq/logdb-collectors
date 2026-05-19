using com.logdb.windows.collector.shared.Contracts;
using LogDB.Extensions.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace com.logdb.windows.collector.ui.Services;

internal static class UiLogDbClientFactory
{
    public static ILogDBClient Create(LogDbConfigDto config, string serviceUrl, ILoggerFactory loggerFactory)
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
            EnableCompression = config.Batch.EnableCompression,
            MaxRetries = Math.Max(0, config.Retry.MaxRetries),
            EnableCircuitBreaker = config.Retry.EnableCircuitBreaker
        };

        return new LogDBClient(Options.Create(options), loggerFactory.CreateLogger<LogDBClient>());
    }
}
