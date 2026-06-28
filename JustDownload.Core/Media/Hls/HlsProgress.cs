namespace JustDownload.Core.Media.Hls;

/// <summary>
/// Progress of an HLS segment download (TASK-037 AC3). Because individual segment sizes are not known up
/// front, progress is reported primarily by segment count (<see cref="CompletedSegments"/> of
/// <see cref="TotalSegments"/>); <see cref="DownloadedBytes"/> is the running byte total for a finer-grained
/// secondary indicator.
/// </summary>
/// <param name="CompletedSegments">How many segments have finished downloading (and decrypting).</param>
/// <param name="TotalSegments">The total number of segments in the playlist.</param>
/// <param name="DownloadedBytes">The cumulative decrypted bytes written so far.</param>
public readonly record struct HlsProgress(int CompletedSegments, int TotalSegments, long DownloadedBytes)
{
    /// <summary>Completion as a 0–1 fraction by segment count (0 when the playlist is empty).</summary>
    public double Fraction => TotalSegments == 0 ? 0 : (double)CompletedSegments / TotalSegments;
}
