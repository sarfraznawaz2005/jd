using System.Net.Http;
using JustDownload.Core.Transport.Proxy;

namespace JustDownload.Core.Transport;

/// <summary>
/// Supplies the <see cref="HttpClient"/> for a given proxy configuration (TASK-034). The direct (no-proxy)
/// client uses the single shared handler (TASK-023); each distinct proxy gets its own pooled handler,
/// created once and reused, so there are only as many handlers as there are proxies in play — keeping the
/// engine light. Clients are cached by configuration value.
/// </summary>
public interface IHttpClientProvider
{
    /// <summary>Returns the cached client routing through <paramref name="proxy"/> (direct for <see cref="ProxyConfiguration.None"/>).</summary>
    HttpClient GetClient(ProxyConfiguration proxy);
}
