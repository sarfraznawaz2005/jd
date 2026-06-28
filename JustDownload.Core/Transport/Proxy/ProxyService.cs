namespace JustDownload.Core.Transport.Proxy;

/// <summary>
/// Default <see cref="IProxyService"/> (TASK-034). The global proxy is a single volatile reference so a
/// runtime change is seen by subsequent requests without restarting (AC2). The per-download override is an
/// <see cref="AsyncLocal{T}"/>, so a value set at the start of a download flows to its probe and every
/// segment worker (each captured via <c>Task.Run</c>) without threading a proxy argument through the
/// transport API, and concurrent downloads with different proxies never interfere.
/// </summary>
internal sealed class ProxyService : IProxyService
{
    private readonly AsyncLocal<ProxyConfiguration?> _override = new();
    private volatile ProxyConfiguration _global = ProxyConfiguration.None;

    public ProxyConfiguration GlobalProxy => _global;

    public ProxyConfiguration Effective => _override.Value ?? _global;

    public void SetGlobalProxy(ProxyConfiguration proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        _global = proxy;
    }

    public IDisposable BeginDownloadScope(ProxyConfiguration? proxy)
    {
        if (proxy is null)
        {
            return NullScope.Instance;
        }

        ProxyConfiguration? previous = _override.Value;
        _override.Value = proxy;
        return new Scope(this, previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly ProxyService _owner;
        private readonly ProxyConfiguration? _previous;

        public Scope(ProxyService owner, ProxyConfiguration? previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose() => _owner._override.Value = _previous;
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
