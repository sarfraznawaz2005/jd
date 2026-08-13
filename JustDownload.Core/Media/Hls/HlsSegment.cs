namespace JustDownload.Core.Media.Hls;

/// <summary>
/// One media segment of an HLS media playlist (TASK-037): its absolute <see cref="Uri"/>, declared
/// <see cref="Duration"/>, the <see cref="MediaSequence"/> number (used to derive the AES IV when the key
/// tag omits one), the <see cref="HlsEncryption"/> in effect for it, and the optional
/// <see cref="ByteRange"/> sub-range of its resource (<c>#EXT-X-BYTERANGE</c>).
/// </summary>
/// <param name="Uri">The absolute segment URL (resolved against the playlist).</param>
/// <param name="Duration">The segment duration in seconds (from <c>#EXTINF</c>).</param>
/// <param name="MediaSequence">The segment's media sequence number.</param>
/// <param name="Encryption">The encryption in effect (the most recent <c>#EXT-X-KEY</c>).</param>
/// <param name="ByteRange">
/// The sub-range of the resource this segment occupies (from <c>#EXT-X-BYTERANGE</c>), or
/// <see langword="null"/> when the segment is the whole resource. Several segments of a byte-range playlist
/// share one resource URI and differ only by this range.
/// </param>
public sealed record HlsSegment(
    Uri Uri,
    double Duration,
    long MediaSequence,
    HlsEncryption Encryption,
    HlsByteRange? ByteRange = null);
