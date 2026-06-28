namespace JustDownload.Core.Lifecycle;

/// <summary>A request to enqueue many downloads at once from pasted text (TASK-074, US-16).</summary>
public sealed record BatchEnqueueRequest
{
    /// <summary>The pasted block of URLs (whitespace-separated, may contain <c>[a-b]</c> range patterns).</summary>
    public required string Text { get; init; }

    /// <summary>The directory every enqueued file is written to.</summary>
    public required string DestinationDirectory { get; init; }

    /// <summary>The referring page URL applied to each download, if any.</summary>
    public string? Referrer { get; init; }

    /// <summary>Per-download maximum connection count applied to each download.</summary>
    public int? MaxConnections { get; init; }

    /// <summary>Per-download speed cap (bytes/sec) applied to each download.</summary>
    public long? SpeedLimit { get; init; }
}

/// <summary>
/// Enqueues many downloads from a pasted block of URLs (TASK-074, US-16 AC3): it expands range patterns via
/// <see cref="BatchUrlExpander"/>, keeps only valid absolute http(s)/ftp(s) URLs, derives a file name for
/// each, and enqueues them through the <see cref="IDownloadManager"/>. Returns the ids created.
/// </summary>
public interface IBatchEnqueuer
{
    /// <summary>Expands and enqueues the URLs in <paramref name="request"/>; returns the created ids in order.</summary>
    Task<IReadOnlyList<long>> EnqueueAsync(BatchEnqueueRequest request, CancellationToken cancellationToken = default);
}
