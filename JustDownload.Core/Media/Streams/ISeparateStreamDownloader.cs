namespace JustDownload.Core.Media.Streams;

/// <summary>
/// Downloads a separate video stream and audio stream concurrently (TASK-039, US-9b): the DASH and
/// two-progressive-URL case. Each stream is downloaded independently through the segmented engine — its own
/// segmentation, progress, segment bar (AC0/AC1) and resume checkpoint — and a failure of one stream does
/// not abort or discard the other, which remains resumable (AC2). The two files are muxed together later
/// (TASK-041).
/// </summary>
public interface ISeparateStreamDownloader
{
    /// <summary>
    /// Downloads <paramref name="video"/> and <paramref name="audio"/> at the same time and returns the
    /// per-stream outcomes. Both are awaited even if one fails; an external cancellation
    /// (<paramref name="cancellationToken"/>) stops both, but one stream's own error never cancels the other.
    /// </summary>
    /// <param name="video">The video stream request (spec + its progress/resume state).</param>
    /// <param name="audio">The audio stream request (spec + its progress/resume state).</param>
    /// <param name="cancellationToken">Cancels both downloads (e.g. user pause/cancel).</param>
    Task<SeparateStreamResult> DownloadAsync(
        StreamDownloadRequest video,
        StreamDownloadRequest audio,
        CancellationToken cancellationToken = default);
}
