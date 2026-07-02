namespace JustDownload.Core.Media.Dash;

/// <summary>
/// One DASH representation JustDownload can download (TASK-039/102). For a <c>BaseURL</c> representation,
/// <see cref="Uri"/> is the resolved absolute media file. For a SegmentTemplate/SegmentList representation
/// (<see cref="DashManifest.IsSegmented"/>), <see cref="Uri"/> instead identifies the manifest plus this
/// representation's id (<see cref="MpdParser.TryParseRepresentationUri"/>) — its segments are resolved by
/// re-parsing the manifest at download time (<see cref="MpdParser.ResolveSegments"/>), the same way an HLS
/// variant playlist URL is re-fetched to get its segments.
/// </summary>
/// <param name="Uri">The absolute media file URL, or the manifest+representation-id identifier.</param>
/// <param name="Bandwidth">The advertised bandwidth in bits/sec.</param>
/// <param name="Width">The frame width in pixels (video only), if declared.</param>
/// <param name="Height">The frame height in pixels (video only), if declared.</param>
/// <param name="Codecs">The codecs string, if declared.</param>
/// <param name="Language">The language tag (audio), if declared.</param>
public sealed record DashRepresentation(
    Uri Uri, long Bandwidth, int? Width, int? Height, string? Codecs, string? Language);

/// <summary>
/// A parsed DASH manifest reduced to the separate video and audio representations JustDownload can download
/// (TASK-039/102). Representations with neither a <c>BaseURL</c> file nor a resolvable
/// SegmentTemplate/SegmentList (e.g. no duration/timeline info, dynamic/live manifests) are omitted — the
/// extractor degrades gracefully when none remain.
/// </summary>
/// <param name="VideoRepresentations">Video representations with downloadable media.</param>
/// <param name="AudioRepresentations">Audio representations with downloadable media.</param>
/// <param name="IsSegmented">
/// Whether any representation resolves via SegmentTemplate/SegmentList rather than a plain <c>BaseURL</c>
/// file. Segmented manifests need the multi-segment download+concat path (<c>MediaKind.Dash</c>, handled by
/// <c>MediaDownloadCoordinator</c>) rather than a single-file HTTP download per stream.
/// </param>
public sealed record DashManifest(
    IReadOnlyList<DashRepresentation> VideoRepresentations,
    IReadOnlyList<DashRepresentation> AudioRepresentations,
    bool IsSegmented = false);
