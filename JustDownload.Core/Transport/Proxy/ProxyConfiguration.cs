using System.Globalization;
using JustDownload.Core.Transport.Auth;

namespace JustDownload.Core.Transport.Proxy;

/// <summary>The kind of proxy to route a download through (TASK-034, US-6).</summary>
public enum ProxyKind
{
    /// <summary>No proxy — connect directly.</summary>
    None = 0,

    /// <summary>An HTTP/HTTPS proxy (<c>CONNECT</c> for HTTPS, absolute-form for HTTP).</summary>
    Http = 1,

    /// <summary>A SOCKS4 proxy (local DNS resolution).</summary>
    Socks4 = 2,

    /// <summary>A SOCKS5 proxy with remote DNS resolution by the proxy (AC1).</summary>
    Socks5 = 3,
}

/// <summary>
/// An immutable proxy setting (TASK-034): the <see cref="Kind"/> and the proxy <see cref="Host"/>/
/// <see cref="Port"/>. <see cref="ToProxyUri"/> maps it to the scheme .NET's <see cref="System.Net.Http.SocketsHttpHandler"/>
/// understands — <c>http://</c>, <c>socks4://</c>, or <c>socks5://</c> (the SOCKS5 scheme makes the handler
/// resolve DNS at the proxy, AC1). Value equality makes it a natural cache key for pooled clients.
/// </summary>
/// <param name="Kind">The proxy kind.</param>
/// <param name="Host">The proxy host, or <see langword="null"/> for <see cref="ProxyKind.None"/>.</param>
/// <param name="Port">The proxy port.</param>
/// <param name="Credentials">Credentials for an authenticating proxy (407), or <see langword="null"/> (TASK-035).</param>
public sealed record ProxyConfiguration(
    ProxyKind Kind, string? Host = null, int Port = 0, NetworkCredentials? Credentials = null)
{
    /// <summary>The shared "no proxy" (direct) configuration.</summary>
    public static ProxyConfiguration None { get; } = new(ProxyKind.None);

    /// <summary>Whether this configuration actually routes through a proxy.</summary>
    public bool IsEnabled => Kind != ProxyKind.None && !string.IsNullOrWhiteSpace(Host);

    /// <summary>The proxy URI for the HTTP handler, or <see langword="null"/> for a direct connection.</summary>
    public Uri? ToProxyUri()
    {
        if (!IsEnabled)
        {
            return null;
        }

        string scheme = Kind switch
        {
            ProxyKind.Http => "http",
            ProxyKind.Socks4 => "socks4",
            ProxyKind.Socks5 => "socks5",
            _ => "http",
        };

        return new Uri(string.Create(CultureInfo.InvariantCulture, $"{scheme}://{Host}:{Port}"));
    }
}
