namespace JustDownload.Core.Media.Extraction;

/// <summary>
/// A pluggable recogniser for one family of media URLs (TASK-036, LOCKED DECISION D3). Extractors are
/// tried by the registry in ascending <see cref="Priority"/> order; the first to return a non-null
/// <see cref="MediaSource"/> wins. This is the seam that keeps the engine open to a future yt-dlp-backed
/// extractor without bundling yt-dlp now: a new extractor is just another registration.
/// </summary>
public interface IMediaExtractor
{
    /// <summary>A short, stable name for diagnostics (e.g. <c>"hls"</c>, <c>"progressive"</c>).</summary>
    string Name { get; }

    /// <summary>
    /// Selection order — lower runs first. Specific extractors (HLS, DASH) use low values so they win over
    /// the catch-all progressive extractor, which uses a high value to act as the fallback.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Returns a <see cref="MediaSource"/> if this extractor recognises <paramref name="request"/>, or
    /// <see langword="null"/> if it does not (so the registry tries the next one). Must never throw for a
    /// URL it simply does not handle — it returns <see langword="null"/> and degrades gracefully (AC2).
    /// </summary>
    /// <param name="request">The candidate URL and its hints.</param>
    /// <param name="cancellationToken">Cancels any network probe the extractor performs.</param>
    Task<MediaSource?> TryExtractAsync(MediaRequest request, CancellationToken cancellationToken = default);
}
