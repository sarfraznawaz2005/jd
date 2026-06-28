namespace JustDownload.Core.Transport.Proxy;

/// <summary>
/// Resolves the proxy for a request (TASK-034, US-6). It holds the <see cref="GlobalProxy"/> that applies
/// to all downloads — changeable at runtime so the proxy can be toggled without restarting the app (AC2) —
/// and supports a per-download override via <see cref="BeginDownloadScope"/>, whose value flows to every
/// transport call made while downloading. <see cref="Effective"/> is the override if one is active,
/// otherwise the global setting.
/// </summary>
public interface IProxyService
{
    /// <summary>The current global proxy applied when a download has no override.</summary>
    ProxyConfiguration GlobalProxy { get; }

    /// <summary>The proxy in effect for the current async flow: the active override, else the global setting.</summary>
    ProxyConfiguration Effective { get; }

    /// <summary>Replaces the global proxy at runtime (takes effect for new requests, no restart — AC2).</summary>
    void SetGlobalProxy(ProxyConfiguration proxy);

    /// <summary>
    /// Begins a per-download proxy override for the current async flow; dispose to clear it. A
    /// <see langword="null"/> proxy means "use the global setting" and installs no override.
    /// </summary>
    IDisposable BeginDownloadScope(ProxyConfiguration? proxy);
}
