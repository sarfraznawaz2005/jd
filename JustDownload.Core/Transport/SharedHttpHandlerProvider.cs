using System.Net;
using System.Net.Http;

namespace JustDownload.Core.Transport;

/// <summary>
/// Builds and owns the one shared <see cref="SocketsHttpHandler"/> (TASK-023). It enables automatic
/// decompression, redirect following, and a per-handler cookie container, and pools/recycles connections
/// per <see cref="TransportOptions"/>. TLS is left at the platform default (negotiates TLS 1.2/1.3).
/// Proxy and authentication wiring layer onto this handler in TASK-034/035.
/// </summary>
internal sealed class SharedHttpHandlerProvider : ISharedHttpHandlerProvider
{
    private readonly SocketsHttpHandler _handler;
    private bool _disposed;

    public SharedHttpHandlerProvider(TransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = options.MaxAutomaticRedirections,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            PooledConnectionLifetime = options.PooledConnectionLifetime,
            PooledConnectionIdleTimeout = options.PooledConnectionIdleTimeout,
            ConnectTimeout = options.ConnectTimeout,
            EnableMultipleHttp2Connections = true,
        };
    }

    public SocketsHttpHandler Handler
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _handler;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handler.Dispose();
    }
}
