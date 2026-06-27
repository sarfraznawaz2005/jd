namespace JustDownload.Core.Media;

/// <summary>
/// One selectable quality of an adaptive video stream (TASK-042): its resolution height and, when known,
/// its advertised bandwidth. HLS/DASH extractors (TASK-037/039) produce these; the quality selector
/// picks one against the user's default video quality.
/// </summary>
/// <param name="Id">An opaque identifier (e.g. the variant playlist URL).</param>
/// <param name="Height">The vertical resolution in pixels (e.g. 1080).</param>
/// <param name="Bandwidth">The advertised bits-per-second, if known (used as a tie-break).</param>
public sealed record VideoVariant(string Id, int Height, long? Bandwidth = null);
