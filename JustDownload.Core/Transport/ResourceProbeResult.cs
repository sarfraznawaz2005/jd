namespace JustDownload.Core.Transport;

/// <summary>
/// What probing a URL revealed before a download starts (TASK-024): the final URL after redirects,
/// whether the server honours byte ranges, the total size (or <see langword="null"/> when unknown), the
/// suggested file name, and the resume validators. The segmentation engine (TASK-026) reads
/// <see cref="CanUseMultipleConnections"/> / <see cref="PlanConnectionCount"/> to decide between a
/// segmented download and the single-connection fallback (US-1 AC3).
/// </summary>
public sealed record ResourceProbeResult
{
    /// <summary>The resource URL after redirects (the URL the download should actually fetch).</summary>
    public required Uri FinalUri { get; init; }

    /// <summary>The status code of the probe response.</summary>
    public required int StatusCode { get; init; }

    /// <summary>
    /// Whether the server honours byte ranges — proven by a <c>206</c> to a one-byte range probe, not
    /// merely advertised via <c>Accept-Ranges</c> (which some servers send but do not honour).
    /// </summary>
    public required bool SupportsRanges { get; init; }

    /// <summary>The total size in bytes, or <see langword="null"/> when the server does not report it.</summary>
    public required long? TotalLength { get; init; }

    /// <summary>The file name derived from <c>Content-Disposition</c> then the URL.</summary>
    public required string SuggestedFileName { get; init; }

    /// <summary>The <c>ETag</c> validator, if any (used for resume/expiry checks).</summary>
    public string? ETag { get; init; }

    /// <summary>The <c>Last-Modified</c> validator, if any.</summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>Whether the server reported a concrete size (an empty resource still counts as known).</summary>
    public bool HasKnownLength => TotalLength.HasValue;

    /// <summary>Whether the download may be paused and resumed later (requires range support).</summary>
    public bool Resumable => SupportsRanges;

    /// <summary>
    /// Whether the download may be split across multiple connections: only when ranges are supported
    /// <i>and</i> a positive total size is known. Otherwise the engine must use a single connection.
    /// </summary>
    public bool CanUseMultipleConnections => SupportsRanges && TotalLength is > 0;

    /// <summary>
    /// Resolves how many connections to actually use given a requested maximum: the request (clamped to
    /// at least one) when segmentation is possible, otherwise <c>1</c> — the single-connection fallback
    /// for range-less or unknown-size resources (US-1 AC3).
    /// </summary>
    /// <param name="requestedConnections">The user's/engine's desired connection count.</param>
    public int PlanConnectionCount(int requestedConnections) =>
        CanUseMultipleConnections ? Math.Max(1, requestedConnections) : 1;
}
