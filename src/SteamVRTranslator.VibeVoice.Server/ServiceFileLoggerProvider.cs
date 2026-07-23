using System.Collections.Concurrent;

namespace SteamVRTranslator.VibeVoice.Server;

internal sealed class ServiceFileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, ServiceFileLogger> _loggers = new();
    private readonly object _sync = new();
    private readonly string _directory;
    private bool _disposed;

    public ServiceFileLoggerProvider(string dataDirectory)
    {
        _directory = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(_directory);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, category => new ServiceFileLogger(this, category));

    public void Dispose()
    {
        _disposed = true;
        _loggers.Clear();
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        if (_disposed || level < LogLevel.Information)
        {
            return;
        }

        var path = Path.Combine(_directory, $"vibevoice-service-{DateTime.Now:yyyyMMdd}.log");
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] [{category}] {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }
        lock (_sync)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    private sealed class ServiceFileLogger(ServiceFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}
