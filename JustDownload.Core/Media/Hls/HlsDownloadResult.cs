namespace JustDownload.Core.Media.Hls;

/// <summary>
/// The outcome of downloading an HLS media playlist's segments (TASK-037). <see cref="SegmentFiles"/> are
/// the decrypted segment files on disk in exact playlist order — the input the concatenator (TASK-038)
/// joins into a single <c>.ts</c>. <see cref="TotalBytes"/> is the total decrypted size.
/// </summary>
/// <param name="SegmentFiles">The decrypted segment file paths, in playlist order.</param>
/// <param name="TotalBytes">The total number of decrypted bytes written across all segments.</param>
public sealed record HlsDownloadResult(IReadOnlyList<string> SegmentFiles, long TotalBytes);
