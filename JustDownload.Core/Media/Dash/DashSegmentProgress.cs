namespace JustDownload.Core.Media.Dash;

/// <summary>
/// Progress of a DASH representation's segment download (TASK-102). Because individual segment sizes are not
/// known up front, progress is reported primarily by segment count (<see cref="CompletedSegments"/> of
/// <see cref="TotalSegments"/>); <see cref="DownloadedBytes"/> is the running byte total for a finer-grained
/// secondary indicator.
/// </summary>
/// <param name="CompletedSegments">How many segments (including the init segment, if any) have finished downloading.</param>
/// <param name="TotalSegments">The total number of segments for the representation.</param>
/// <param name="DownloadedBytes">The cumulative bytes written so far.</param>
public readonly record struct DashSegmentProgress(int CompletedSegments, int TotalSegments, long DownloadedBytes)
{
    /// <summary>Completion as a 0–1 fraction by segment count (0 when there are no segments).</summary>
    public double Fraction => TotalSegments == 0 ? 0 : (double)CompletedSegments / TotalSegments;
}
