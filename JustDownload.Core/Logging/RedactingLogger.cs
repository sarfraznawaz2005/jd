using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Logging;

/// <summary>
/// An <see cref="ILogger"/> decorator that runs every formatted message through an
/// <see cref="ISecretRedactor"/> before it reaches the underlying logger (and therefore every
/// provider — console, debug, file). This is how CLAUDE.md §5's "logs redact auth headers, tokens,
/// and signed-URL query strings" is enforced uniformly, regardless of which sink is configured.
/// </summary>
internal sealed class RedactingLogger : ILogger
{
    private readonly ILogger _inner;
    private readonly ISecretRedactor _redactor;

    public RedactingLogger(ILogger inner, ISecretRedactor redactor)
    {
        _inner = inner;
        _redactor = redactor;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        // Redact the final rendered message. We wrap the caller's formatter so the masking applies
        // to the fully-composed string (template + structured values) that the sink would print.
        _inner.Log(logLevel, eventId, state, exception, (s, e) => _redactor.Redact(formatter(s, e)));
    }
}
