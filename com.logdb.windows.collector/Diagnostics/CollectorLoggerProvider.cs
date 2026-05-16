namespace com.logdb.windows.collector.Diagnostics;

public sealed class CollectorLoggerProvider : ILoggerProvider
{
    private readonly CollectorLogSink _sink;

    public CollectorLoggerProvider(CollectorLogSink sink)
    {
        _sink = sink;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new CollectorLogger(categoryName, _sink);
    }

    public void Dispose()
    {
    }

    private sealed class CollectorLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly CollectorLogSink _sink;

        public CollectorLogger(string categoryName, CollectorLogSink sink)
        {
            _categoryName = categoryName;
            _sink = sink;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Information;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception != null)
            {
                message = $"{message}{Environment.NewLine}{exception}";
            }

            _sink.Write(logLevel, _categoryName, message);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}
