namespace JustDownload.Core.Media.Extraction;

/// <summary>
/// What one <see cref="IMediaExtractor"/> did with a <see cref="MediaRequest"/>. The registry records one
/// of these per extractor it tried so the caller can explain <em>why</em> extraction produced nothing,
/// instead of collapsing every distinct cause into a single "no media found" (no silent failures, §5).
/// </summary>
public enum MediaExtractionOutcome
{
    /// <summary>The extractor recognised the URL and produced a <see cref="MediaSource"/>.</summary>
    Accepted,

    /// <summary>The extractor does not handle this URL — the normal, uninteresting case.</summary>
    Declined,

    /// <summary>
    /// The extractor recognised the URL (or was already committed to it) but could not extract media, and
    /// said why — e.g. yt-dlp exiting non-zero, or an unexpected exception.
    /// </summary>
    Failed,

    /// <summary>
    /// The extractor could not reach the network at all: DNS resolution failure, a refused/reset
    /// connection, or a timeout. Distinct from <see cref="Declined"/> because it says nothing about
    /// whether media exists at the URL.
    /// </summary>
    NetworkFailure,
}
