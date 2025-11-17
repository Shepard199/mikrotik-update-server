using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using MikroTik.UpdateServer.Models;

namespace MikroTik.UpdateServer.Services
{
    /// <summary>
    /// ILoggerProvider, который пишет все логи в ILogStore,
    /// чтобы их было видно в Log Viewer (/api/logs).
    /// </summary>
    public class LogStoreLoggerProvider : ILoggerProvider
    {
        private readonly ILogStore _logStore;
        private readonly ConcurrentDictionary<string, LogStoreLogger> _loggers = new();

        public LogStoreLoggerProvider(ILogStore logStore)
        {
            _logStore = logStore;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return _loggers.GetOrAdd(categoryName, name => new LogStoreLogger(name, _logStore));
        }

        public void Dispose()
        {
            _loggers.Clear();
        }

        private sealed class LogStoreLogger : ILogger
        {
            private readonly string _categoryName;
            private readonly ILogStore _store;

            public LogStoreLogger(string categoryName, ILogStore store)
            {
                _categoryName = categoryName;
                _store = store;
            }

            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel)
            {
                // Можно подрезать шум: не пишем Trace и None
                return logLevel != LogLevel.None && logLevel != LogLevel.Trace;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                    return;

                if (formatter == null)
                    throw new ArgumentNullException(nameof(formatter));

                var message = formatter(state, exception);
                if (string.IsNullOrEmpty(message) && exception == null)
                    return;

                // Маппинг в те уровни, которые ожидает фронт
                var levelString = logLevel switch
                {
                    LogLevel.Critical => "Error",
                    LogLevel.Error => "Error",
                    LogLevel.Warning => "Warning",
                    LogLevel.Information => "Information",
                    LogLevel.Debug => "Debug",
                    LogLevel.Trace => "Trace",
                    _ => "Information"
                };

                _store.Add(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = levelString,
                    Source = _categoryName,
                    Message = message,
                    Exception = exception?.ToString()
                });
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }
}
