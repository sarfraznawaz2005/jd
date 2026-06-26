using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace JustDownload.Tests.Logging;

/// <summary>
/// A minimal in-memory <see cref="ILoggerProvider"/> used to assert what actually reaches a log
/// sink — i.e. the fully-formatted message string after the redacting pipeline has run.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
        // Nothing to dispose; entries are plain in-memory state.
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly CapturingLoggerProvider _owner;

        public CapturingLogger(CapturingLoggerProvider owner) => _owner = owner;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _owner.Entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
