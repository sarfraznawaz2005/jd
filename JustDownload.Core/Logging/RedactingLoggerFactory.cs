using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Logging;

/// <summary>
/// An <see cref="ILoggerFactory"/> decorator that hands out <see cref="RedactingLogger"/> instances,
/// so every <see cref="ILogger"/> resolved from the container redacts secrets before logging — no
/// matter which category or provider is in play. Registered as the composition root's
/// <see cref="ILoggerFactory"/> (see <see cref="ServiceCollectionExtensions"/>).
/// </summary>
internal sealed class RedactingLoggerFactory : ILoggerFactory
{
    private readonly ILoggerFactory _inner;
    private readonly ISecretRedactor _redactor;

    public RedactingLoggerFactory(ILoggerFactory inner, ISecretRedactor redactor)
    {
        _inner = inner;
        _redactor = redactor;
    }

    public void AddProvider(ILoggerProvider provider) => _inner.AddProvider(provider);

    public ILogger CreateLogger(string categoryName)
        => new RedactingLogger(_inner.CreateLogger(categoryName), _redactor);

    public void Dispose() => _inner.Dispose();
}
