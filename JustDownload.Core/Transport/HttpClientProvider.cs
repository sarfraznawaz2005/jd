using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using JustDownload.Core.Transport.Proxy;

namespace JustDownload.Core.Transport;

/// <summary>
/// Default <see cref="IHttpClientProvider"/> (TASK-034). The direct client wraps the shared
/// <see cref="SocketsHttpHandler"/> (one connection pool for the common no-proxy case, TASK-023). For a
/// proxy it lazily creates a dedicated handler — cloning the shared tuning and setting an
/// <see cref="IWebProxy"/> built from the configuration's URI (HTTP or SOCKS) — and caches the resulting
/// client by configuration, so repeated downloads through the same proxy reuse one pool. All created
/// handlers/clients are disposed with the provider.
/// </summary>
internal sealed class HttpClientProvider : IHttpClientProvider, IDisposable
{
    private readonly ConcurrentDictionary<ProxyConfiguration, HttpClient> _clients = new();
    private readonly List<SocketsHttpHandler> _ownedHandlers = [];
    private readonly object _ownedGate = new();
    private readonly ISharedHttpHandlerProvider _sharedHandler;
    private readonly TransportOptions _options;
    private bool _disposed;

    public HttpClientProvider(ISharedHttpHandlerProvider sharedHandler, TransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(sharedHandler);
        ArgumentNullException.ThrowIfNull(options);
        _sharedHandler = sharedHandler;
        _options = options;
    }

    public HttpClient GetClient(ProxyConfiguration proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _clients.GetOrAdd(proxy, CreateClient);
    }

    private HttpClient CreateClient(ProxyConfiguration proxy)
    {
        HttpClient client = proxy.IsEnabled
            ? new HttpClient(CreateProxiedHandler(proxy), disposeHandler: false)
            : new HttpClient(_sharedHandler.Handler, disposeHandler: false);

        client.Timeout = Timeout.InfiniteTimeSpan; // large transfers are bounded by cancellation, not a clock
        client.DefaultRequestHeaders.UserAgent.ParseAdd(_options.UserAgent);
        return client;
    }

    private SocketsHttpHandler CreateProxiedHandler(ProxyConfiguration proxy)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = _options.MaxAutomaticRedirections,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            PooledConnectionLifetime = _options.PooledConnectionLifetime,
            PooledConnectionIdleTimeout = _options.PooledConnectionIdleTimeout,
            ConnectTimeout = _options.ConnectTimeout,
            EnableMultipleHttp2Connections = true,
            UseProxy = true,
            Proxy = new WebProxy(proxy.ToProxyUri()),
        };

        lock (_ownedGate)
        {
            _ownedHandlers.Add(handler);
        }

        return handler;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (HttpClient client in _clients.Values)
        {
            client.Dispose();
        }

        lock (_ownedGate)
        {
            foreach (SocketsHttpHandler handler in _ownedHandlers)
            {
                handler.Dispose();
            }
        }
    }
}
