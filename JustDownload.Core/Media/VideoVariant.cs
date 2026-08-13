namespace JustDownload.Core.Media;

/// <summary>
/// One selectable quality of an adaptive video stream (TASK-042): its resolution height and, when known,
/// its advertised bandwidth. HLS/DASH extractors (TASK-037/039) produce these; the quality selector
/// picks one against the user's default video quality.
/// </summary>
/// <param name="Id">An opaque identifier (e.g. the variant playlist URL).</param>
/// <param name="Height">The vertical resolution in pixels (e.g. 1080).</param>
/// <param name="Bandwidth">The advertised bits-per-second, if known (used as a tie-break).</param>
/// <param name="Fps">The frame rate, if known (yt-dlp only; other extractors leave this <see langword="null"/>).</param>
/// <param name="Codec">
/// A friendly video codec label (e.g. "H.264", "VP9", "AV1"), if known — used to distinguish otherwise
/// identical-looking same-resolution renditions in the quality picker (TASK-166). <see langword="null"/>
/// when the extractor doesn't report a codec.
/// </param>
public sealed record VideoVariant(string Id, int Height, long? Bandwidth = null, double? Fps = null, string? Codec = null);
