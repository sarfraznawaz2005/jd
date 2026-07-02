namespace JustDownload.Core.Media.Dash;

/// <summary>
/// The outcome of downloading a DASH representation's segments (TASK-102). <see cref="SegmentFiles"/> are the
/// downloaded segment files on disk in order (the init segment first, if the representation has one) — the
/// input a concatenator joins into a single stream file for muxing. <see cref="TotalBytes"/> is the total size.
/// </summary>
/// <param name="SegmentFiles">The downloaded segment file paths, in order.</param>
/// <param name="TotalBytes">The total number of bytes written across all segments.</param>
public sealed record DashSegmentDownloadResult(IReadOnlyList<string> SegmentFiles, long TotalBytes);
