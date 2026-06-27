namespace JustDownload.Core.Downloading;

/// <summary>
/// Downloads a resource to a file using dynamic segmentation with work-stealing (TASK-026): probes the
/// URL, splits it across connections, and lets each idle connection re-split the largest remaining range
/// so the slowest segment never holds up completion — saturating available bandwidth (US-1). Falls back
/// to a single connection when the server does not support ranges or the size is unknown.
/// </summary>
public interface ISegmentedDownloader
{
    /// <summary>
    /// Runs the download to completion and returns its result. <paramref name="progress"/> receives the
    /// running total of bytes written.
    /// <para>
    /// When <paramref name="received"/> is supplied for a multi-connection download it serves two roles
    /// (TASK-028, US-2): if it is pre-seeded with previously-downloaded intervals the download resumes,
    /// fetching only the remaining gaps; and as bytes are written each committed range is recorded into it,
    /// so the caller can persist the resume checkpoint and pause/resume without re-fetching. A
    /// single-connection (range-less/unknown-size) transfer cannot resume and ignores it.
    /// </para>
    /// </summary>
    /// <param name="request">What to download and where.</param>
    /// <param name="progress">Optional sink for cumulative bytes-written updates.</param>
    /// <param name="received">Optional resume checkpoint: seeds resume and records committed writes.</param>
    /// <param name="cancellationToken">Cancels the download (honored promptly for pause/cancel).</param>
    Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<long>? progress = null,
        ReceivedRanges? received = null,
        CancellationToken cancellationToken = default);
}
