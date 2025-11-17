using System.Collections.Concurrent;
using MikroTik.UpdateServer.Models;

namespace MikroTik.UpdateServer.Services;

/// <summary>
///     ILoggerProvider, который пишет все логи в ILogStore,
///     чтобы их было видно в Log Viewer (/api/logs).
/// </summary>
public class LogStoreLoggerProvider(ILogStore logStore) : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, LogStoreLogger> _loggers = new();

    // Простая защита от использования после Dispose
    private bool _disposed;

    public ILogger CreateLogger(string categoryName)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(LogStoreLoggerProvider));

        return _loggers.GetOrAdd(categoryName, name => new LogStoreLogger(name, logStore));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _loggers.Clear();
    }

    private sealed class LogStoreLogger(string categoryName, ILogStore store) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

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
            Func<TState, Exception?, string>? formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            if (formatter is null)
                return;

            var message = formatter(state, exception);

            // Если нет ни сообщения, ни исключения — не пишем мусор
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
                _ => "Information"
            };

            try
            {
                store.Add(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = levelString,
                    Source = categoryName,
                    Message = message ?? string.Empty,
                    Exception = exception?.ToString()
                });
            }
            catch
            {
                // Если стор сломался, просто пропускаем запись.
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
}