using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIRsLight.ClashOfRim.Network;

public sealed class ClashOfRimFileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, ClashOfRimFileLogger> loggers = new(StringComparer.Ordinal);
    private readonly object syncRoot = new();
    private readonly StreamWriter writer;
    private bool disposed;

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
        lock (syncRoot)
        {
            return disposed ? NullLogger.Instance
                : loggers.GetOrAdd(categoryName, name => new ClashOfRimFileLogger(name, this));
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed) return;
            disposed = true;
            loggers.Clear();
            writer.Dispose();
        }
    }

    private sealed class ClashOfRimFileLogger : ILogger
    {
        private readonly string categoryName;
        private readonly ClashOfRimFileLoggerProvider owner;
        private readonly StreamWriter writer;
        private readonly object syncRoot;

        public ClashOfRimFileLogger(string categoryName, ClashOfRimFileLoggerProvider owner)
        {
            this.categoryName = categoryName;
            this.owner = owner;
            writer = owner.writer;
            syncRoot = owner.syncRoot;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None && !Volatile.Read(ref owner.disposed);
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
                // Formatting runs outside the sink lock and may finish after shutdown.
                if (owner.disposed) return;
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
