using JustDownload.Core.Transport.Proxy;

namespace JustDownload.Core.Downloading;

/// <summary>
/// A request to download one resource to a file (TASK-026): the source <see cref="Url"/>, the
/// <see cref="DestinationPath"/> to write, the desired number of <see cref="Connections"/>, and any
/// extra request <see cref="Headers"/> (cookies/referrer from the browser extension, etc.).
/// </summary>
public sealed record DownloadRequest
{
    /// <summary>The resource URL.</summary>
    public required Uri Url { get; init; }

    /// <summary>The absolute path of the file to write.</summary>
    public required string DestinationPath { get; init; }

    /// <summary>The requested connection count (clamped to 1–32), or <see langword="null"/> for the default.</summary>
    public int? Connections { get; init; }

    /// <summary>A per-download speed cap in bytes/sec, or <see langword="null"/>/<c>0</c> for no per-download cap.</summary>
    public long? SpeedLimit { get; init; }

    /// <summary>Extra request headers to send on every connection.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];

    /// <summary>
    /// A per-download proxy override (TASK-034, US-6), or <see langword="null"/> to use the global proxy
    /// setting. Applies to all of this download's connections.
    /// </summary>
    public ProxyConfiguration? Proxy { get; init; }
}
