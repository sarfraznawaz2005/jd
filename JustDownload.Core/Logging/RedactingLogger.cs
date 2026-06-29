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
    private readonly ILogLevelSwitch _levelSwitch;

    public RedactingLogger(ILogger inner, ISecretRedactor redactor, ILogLevelSwitch levelSwitch)
    {
        _inner = inner;
        _redactor = redactor;
        _levelSwitch = levelSwitch;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => _inner.BeginScope(state);

    // The runtime level switch is the authority (TASK-127); the inner logger is configured wide-open so the
    // switch can both raise and lower verbosity live.
    public bool IsEnabled(LogLevel logLevel) =>
        logLevel >= _levelSwitch.Minimum && _inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        if (!IsEnabled(logLevel))
        {
            return; // honor the live switch even for direct Log() calls that skip the IsEnabled pre-check
        }

        // Redact the final rendered message. We wrap the caller's formatter so the masking applies
        // to the fully-composed string (template + structured values) that the sink would print.
        _inner.Log(logLevel, eventId, state, exception, (s, e) => _redactor.Redact(formatter(s, e)));
    }
}
