namespace JustDownload.Core.Media.Hls;

/// <summary>
/// One media segment of an HLS media playlist (TASK-037): its absolute <see cref="Uri"/>, declared
/// <see cref="Duration"/>, the <see cref="MediaSequence"/> number (used to derive the AES IV when the key
/// tag omits one), and the <see cref="HlsEncryption"/> in effect for it.
/// </summary>
/// <param name="Uri">The absolute segment URL (resolved against the playlist).</param>
/// <param name="Duration">The segment duration in seconds (from <c>#EXTINF</c>).</param>
/// <param name="MediaSequence">The segment's media sequence number.</param>
/// <param name="Encryption">The encryption in effect (the most recent <c>#EXT-X-KEY</c>).</param>
public sealed record HlsSegment(Uri Uri, double Duration, long MediaSequence, HlsEncryption Encryption);
