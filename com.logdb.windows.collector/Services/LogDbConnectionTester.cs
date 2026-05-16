using com.logdb.windows.collector.shared.Contracts;
using LogDB.Client.Models;
using LogDB.Extensions.Logging;
using LogLevel = LogDB.Client.Models.LogLevel;

namespace com.logdb.windows.collector.Services;

public interface ILogDbConnectionTester
{
    Task<(bool Success, string Message)> TestAsync(CollectorConfigDto config, CancellationToken cancellationToken = default);
}

public sealed class LogDbConnectionTester : ILogDbConnectionTester
{
    private readonly ILogDbServiceUrlResolver _serviceUrlResolver;
    private readonly ILoggerFactory _loggerFactory;

    public LogDbConnectionTester(
        ILogDbServiceUrlResolver serviceUrlResolver,
        ILoggerFactory loggerFactory)
    {
        _serviceUrlResolver = serviceUrlResolver;
        _loggerFactory = loggerFactory;
    }

    public async Task<(bool Success, string Message)> TestAsync(
        CollectorConfigDto config,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.LogDB.ApiKey))
        {
            return (false, "LogDB API key is missing.");
        }

        var serviceUrl = await _serviceUrlResolver.ResolveAsync(config.LogDB, cancellationToken);
        var client = LogDbClientFactory.Create(config.LogDB, serviceUrl, _loggerFactory);

        var log = new Log
        {
            Guid = Guid.NewGuid().ToString(),
            Timestamp = DateTime.UtcNow,
            Application = "LogDB Windows Collector",
            Environment = config.Server.ServerEnvironment,
            Level = LogLevel.Info,
            Message = "Collector connection test",
            Source = "collector/control",
            Collection = "collector-diagnostics",
            Label = new List<string> { "collector", "connection-test" },
            AttributesS = new Dictionary<string, string>
            {
                ["serverName"] = config.Server.ServerName,
                ["_sys_type"] = "collector_diagnostic"
            }
        };

        var result = await client.LogAsync(log, cancellationToken);
        await client.FlushAsync();

        return result == LogResponseStatus.Success
            ? (true, $"Connection succeeded via {serviceUrl}.")
            : (false, $"Connection failed with status: {result}.");
    }
}
