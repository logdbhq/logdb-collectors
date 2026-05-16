namespace com.logdb.windows.collector.Diagnostics;

internal sealed class ForwardingLoggerProvider : ILoggerProvider
{
    private readonly ILoggerFactory _loggerFactory;

    public ForwardingLoggerProvider(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggerFactory.CreateLogger(categoryName);
    }

    public void Dispose()
    {
    }
}
