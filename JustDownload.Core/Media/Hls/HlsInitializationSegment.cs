namespace JustDownload.Core.Media.Hls;

/// <summary>
/// The <c>#EXT-X-MAP</c> initialization segment of a media playlist: the resource holding the
/// <c>ftyp</c>/<c>moov</c> boxes a fragmented-MP4 (CMAF) stream needs, and — when the tag carries a
/// <c>BYTERANGE</c> attribute — the sub-range of it that actually is the initialization data.
/// </summary>
/// <param name="Uri">The absolute initialization segment URL (resolved against the playlist).</param>
/// <param name="ByteRange">The sub-range to fetch, or <see langword="null"/> for the whole resource.</param>
public sealed record HlsInitializationSegment(Uri Uri, HlsByteRange? ByteRange = null);
