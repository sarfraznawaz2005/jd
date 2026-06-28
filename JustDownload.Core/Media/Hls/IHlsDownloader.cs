namespace JustDownload.Core.Media.Hls;

/// <summary>
/// Downloads an HLS media playlist into decrypted segment files (TASK-037, US-9): fetches and parses the
/// media playlist, downloads every <c>.ts</c> segment in parallel (AC1), decrypts AES-128 segments (AC2),
/// and reports progress by segment count (AC3). Concatenation into a single file is a separate step
/// (TASK-038). Honours cancellation promptly so pause/cancel is instant.
/// </summary>
public interface IHlsDownloader
{
    /// <summary>
    /// Downloads all segments of the media playlist at <paramref name="mediaPlaylistUri"/> into
    /// <paramref name="workingDirectory"/>, returning the ordered decrypted segment files.
    /// </summary>
    /// <param name="mediaPlaylistUri">The media playlist URL (a chosen variant, or a direct media playlist).</param>
    /// <param name="workingDirectory">A directory to write the decrypted segment files into (created if absent).</param>
    /// <param name="headers">Extra request headers (cookies/referrer) to replay on every request.</param>
    /// <param name="progress">Optional sink for segment-count progress.</param>
    /// <param name="cancellationToken">Cancels the download promptly.</param>
    /// <exception cref="HlsExtractionException">The playlist could not be fetched/parsed, or uses unsupported encryption.</exception>
    Task<HlsDownloadResult> DownloadAsync(
        Uri mediaPlaylistUri,
        string workingDirectory,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null,
        IProgress<HlsProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Thrown when HLS extraction/download fails in a way the user should see (US-9, "honest extraction").</summary>
public sealed class HlsExtractionException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public HlsExtractionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner cause.</summary>
    public HlsExtractionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
