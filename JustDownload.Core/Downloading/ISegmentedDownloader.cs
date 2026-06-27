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
    /// </summary>
    /// <param name="request">What to download and where.</param>
    /// <param name="progress">Optional sink for cumulative bytes-written updates.</param>
    /// <param name="cancellationToken">Cancels the download (honored promptly for pause/cancel).</param>
    Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);
}
