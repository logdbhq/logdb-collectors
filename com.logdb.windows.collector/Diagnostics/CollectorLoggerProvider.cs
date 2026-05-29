namespace com.logdb.windows.collector.Diagnostics;

public sealed class CollectorLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    /// <summary>
    /// Well-known scope/state key carrying the timestamp of the underlying log
    /// record (the time the log itself carries, not when the collector logged
    /// about it). Exporters attach this via <c>BeginScope</c> on a per-record
    /// log line; the value may be a <see cref="DateTime"/> or
    /// <see cref="DateTimeOffset"/>. Kept as a string literal so the otherwise
    /// standalone exporter libraries don't take a dependency on the collector.
    /// </summary>
    public const string EventTimestampKey = "LogEventTimestamp";

    private readonly CollectorLogSink _sink;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public CollectorLoggerProvider(CollectorLogSink sink)
    {
        _sink = sink;
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new CollectorLogger(categoryName, _sink, () => _scopeProvider);
    }

    public void Dispose()
    {
    }

    private sealed class CollectorLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly CollectorLogSink _sink;
        private readonly Func<IExternalScopeProvider> _scopeProvider;

        public CollectorLogger(string categoryName, CollectorLogSink sink, Func<IExternalScopeProvider> scopeProvider)
        {
            _categoryName = categoryName;
            _sink = sink;
            _scopeProvider = scopeProvider;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return _scopeProvider().Push(state) ?? NullScope.Instance;
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

            // The event timestamp may be carried either directly on the log
            // state (a structured property) or on an enclosing scope.
            var eventTimestamp = ExtractEventTimestamp(state) ?? ExtractEventTimestampFromScopes();

            _sink.Write(logLevel, _categoryName, message, eventTimestamp);
        }

        private DateTime? ExtractEventTimestampFromScopes()
        {
            DateTime? found = null;
            _scopeProvider().ForEachScope(
                (scope, _) =>
                {
                    var candidate = ExtractEventTimestamp(scope);
                    if (candidate.HasValue)
                    {
                        found = candidate;
                    }
                },
                (object?)null);
            return found;
        }

        private static DateTime? ExtractEventTimestamp(object? state)
        {
            if (state is not IEnumerable<KeyValuePair<string, object>> pairs)
            {
                return null;
            }

            foreach (var pair in pairs)
            {
                if (!string.Equals(pair.Key, EventTimestampKey, StringComparison.Ordinal))
                {
                    continue;
                }

                return pair.Value switch
                {
                    DateTime dt => dt.ToUniversalTime(),
                    DateTimeOffset dto => dto.UtcDateTime,
                    _ => null
                };
            }

            return null;
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
