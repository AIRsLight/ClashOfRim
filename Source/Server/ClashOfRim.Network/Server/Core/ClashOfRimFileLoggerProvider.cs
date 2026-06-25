using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network;

public sealed class ClashOfRimFileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, ClashOfRimFileLogger> loggers = new(StringComparer.Ordinal);
    private readonly object syncRoot = new();
    private readonly StreamWriter writer;

    public ClashOfRimFileLoggerProvider(string logFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);
        string? directory = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        LogFilePath = logFilePath;
        writer = new StreamWriter(new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
    }

    public string LogFilePath { get; }

    public ILogger CreateLogger(string categoryName)
    {
        return loggers.GetOrAdd(categoryName, name => new ClashOfRimFileLogger(name, writer, syncRoot));
    }

    public void Dispose()
    {
        writer.Dispose();
    }

    private sealed class ClashOfRimFileLogger : ILogger
    {
        private readonly string categoryName;
        private readonly StreamWriter writer;
        private readonly object syncRoot;

        public ClashOfRimFileLogger(string categoryName, StreamWriter writer, object syncRoot)
        {
            this.categoryName = categoryName;
            this.writer = writer;
            this.syncRoot = syncRoot;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
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

            string message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception is null)
            {
                return;
            }

            lock (syncRoot)
            {
                writer.Write(DateTimeOffset.Now.ToString("O"));
                writer.Write(" [");
                writer.Write(logLevel);
                writer.Write("] ");
                writer.Write(categoryName);
                if (eventId.Id != 0 || !string.IsNullOrWhiteSpace(eventId.Name))
                {
                    writer.Write(" ");
                    writer.Write(eventId.Id);
                    if (!string.IsNullOrWhiteSpace(eventId.Name))
                    {
                        writer.Write(":");
                        writer.Write(eventId.Name);
                    }
                }

                writer.Write(" - ");
                writer.WriteLine(message);
                if (exception is not null)
                {
                    writer.WriteLine(exception.ToString());
                }
            }
        }
    }
}
