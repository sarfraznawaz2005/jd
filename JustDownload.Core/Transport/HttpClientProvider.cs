using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using JustDownload.Core.Transport.Auth;
using JustDownload.Core.Transport.Proxy;

namespace JustDownload.Core.Transport;

/// <summary>
/// Default <see cref="IHttpClientProvider"/> (TASK-034/035). The direct, unauthenticated profile wraps the
/// shared <see cref="SocketsHttpHandler"/> (one connection pool for the common case, TASK-023). Any profile
/// involving a proxy or credentials gets a dedicated handler — cloning the shared tuning, setting an
/// <see cref="IWebProxy"/> (with proxy credentials for 407) and the origin <see cref="ICredentials"/> that
/// .NET uses to answer Basic/Digest/NTLM challenges — cached by profile so repeated downloads with the same
/// profile reuse one pool. All created handlers/clients are disposed with the provider.
/// </summary>
internal sealed class HttpClientProvider : IHttpClientProvider, IDisposable
{
    private readonly ConcurrentDictionary<ConnectionProfile, HttpClient> _clients = new();
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

    public HttpClient GetClient(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _clients.GetOrAdd(profile, CreateClient);
    }

    private HttpClient CreateClient(ConnectionProfile profile)
    {
        HttpClient client = profile.RequiresDedicatedHandler
            ? new HttpClient(CreateDedicatedHandler(profile), disposeHandler: false)
            : new HttpClient(_sharedHandler.Handler, disposeHandler: false);

        client.Timeout = Timeout.InfiniteTimeSpan; // large transfers are bounded by cancellation, not a clock
        client.DefaultRequestHeaders.UserAgent.ParseAdd(_options.UserAgent);
        return client;
    }

    private SocketsHttpHandler CreateDedicatedHandler(ConnectionProfile profile)
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
        };

        if (profile.Proxy.IsEnabled)
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(profile.Proxy.ToProxyUri())
            {
                Credentials = ToNetworkCredential(profile.Proxy.Credentials),
            };
        }

        // .NET answers the server's 401 challenge (Basic/Digest/NTLM/Negotiate) using these credentials.
        handler.Credentials = ToNetworkCredential(profile.Credentials);

        lock (_ownedGate)
        {
            _ownedHandlers.Add(handler);
        }

        return handler;
    }

    private static NetworkCredential? ToNetworkCredential(NetworkCredentials? credentials) =>
        credentials is null
            ? null
            : new NetworkCredential(credentials.Username, credentials.Password, credentials.Domain ?? string.Empty);

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
