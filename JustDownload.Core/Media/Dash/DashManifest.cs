namespace JustDownload.Core.Media.Dash;

/// <summary>
/// One DASH representation resolved to a directly-downloadable file (TASK-039): a representation whose
/// media is addressed by a <c>BaseURL</c> (the progressive / single-file case JustDownload downloads and
/// muxes). <see cref="Uri"/> is the resolved absolute file URL.
/// </summary>
/// <param name="Uri">The absolute media file URL (resolved BaseURL chain).</param>
/// <param name="Bandwidth">The advertised bandwidth in bits/sec.</param>
/// <param name="Width">The frame width in pixels (video only), if declared.</param>
/// <param name="Height">The frame height in pixels (video only), if declared.</param>
/// <param name="Codecs">The codecs string, if declared.</param>
/// <param name="Language">The language tag (audio), if declared.</param>
public sealed record DashRepresentation(
    Uri Uri, long Bandwidth, int? Width, int? Height, string? Codecs, string? Language);

/// <summary>
/// A parsed DASH manifest reduced to the separate video and audio representations JustDownload can download
/// directly (TASK-039). SegmentTemplate/SegmentList-only representations (no BaseURL file) are omitted —
/// they are out of scope for the progressive separate-streams path and the extractor degrades gracefully
/// when none remain.
/// </summary>
/// <param name="VideoRepresentations">Video representations with a downloadable file.</param>
/// <param name="AudioRepresentations">Audio representations with a downloadable file.</param>
public sealed record DashManifest(
    IReadOnlyList<DashRepresentation> VideoRepresentations,
    IReadOnlyList<DashRepresentation> AudioRepresentations);
