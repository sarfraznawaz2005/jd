namespace JustDownload.Core.Transport;

/// <summary>
/// Tunables for the shared HTTP handler (TASK-023). Defaults are chosen for a download manager:
/// connections are pooled and recycled, redirects are followed, and there is no overall request timeout
/// (large downloads are bounded by cancellation, not a clock). TLS is left at the platform default,
/// which negotiates TLS 1.2/1.3.
/// </summary>
public sealed class TransportOptions
{
    /// <summary>Maximum redirects to follow before failing. Default 10.</summary>
    public int MaxAutomaticRedirections { get; set; } = 10;

    /// <summary>How long a pooled connection may live before being recycled. Default 2 minutes.</summary>
    public TimeSpan PooledConnectionLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long an idle pooled connection is kept. Default 90 seconds.</summary>
    public TimeSpan PooledConnectionIdleTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>TCP connect timeout for establishing a new connection. Default 30 seconds.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The <c>User-Agent</c> sent when a request does not specify one. Default JustDownload.</summary>
    public string UserAgent { get; set; } = "JustDownload";
}
