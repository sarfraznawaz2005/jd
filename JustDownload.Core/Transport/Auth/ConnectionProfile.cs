using JustDownload.Core.Transport.Proxy;

namespace JustDownload.Core.Transport.Auth;

/// <summary>
/// The full set of connection-shaping options for a request (TASK-034/035): the <see cref="Proxy"/> (with
/// its own optional credentials) and the origin <see cref="Credentials"/>. It is the cache key for the
/// pooled HTTP clients — a distinct proxy/credential combination gets its own handler so .NET can carry the
/// right <c>Credentials</c> (for Basic/Digest/NTLM challenge-response) and proxy credentials. Value equality
/// makes equal profiles share one pooled client.
/// </summary>
/// <param name="Proxy">The proxy configuration (its own credentials cover proxy 407 auth).</param>
/// <param name="Credentials">The origin server credentials for 401 auth, or <see langword="null"/>.</param>
public sealed record ConnectionProfile(ProxyConfiguration Proxy, NetworkCredentials? Credentials = null)
{
    /// <summary>The direct, unauthenticated profile (no proxy, no credentials).</summary>
    public static ConnectionProfile Direct { get; } = new(ProxyConfiguration.None);

    /// <summary>Whether this profile needs a dedicated handler (a proxy or any credentials are involved).</summary>
    public bool RequiresDedicatedHandler =>
        Proxy.IsEnabled || Credentials is not null || Proxy.Credentials is not null;
}
