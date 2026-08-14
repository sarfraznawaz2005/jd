namespace JustDownload.Core.Media.Hls;

/// <summary>
/// One variant of an HLS master playlist (TASK-037 AC0): the <see cref="Uri"/> of its media playlist plus
/// the attributes used to pick a quality — advertised <see cref="Bandwidth"/>, optional resolution
/// (<see cref="Width"/>×<see cref="Height"/>), and the <see cref="Codecs"/> string.
/// </summary>
/// <param name="Uri">The absolute URI of the variant's media playlist.</param>
/// <param name="Bandwidth">The advertised peak bandwidth in bits/sec (from <c>BANDWIDTH</c>).</param>
/// <param name="Width">The frame width in pixels, if a <c>RESOLUTION</c> was given.</param>
/// <param name="Height">The frame height in pixels, if a <c>RESOLUTION</c> was given.</param>
/// <param name="Codecs">The <c>CODECS</c> attribute, if present.</param>
public sealed record HlsVariant(Uri Uri, long Bandwidth, int? Width, int? Height, string? Codecs);

/// <summary>
/// An alternate audio rendition from a master playlist's <c>#EXT-X-MEDIA:TYPE=AUDIO</c> entries (RFC 8216
/// §4.3.4.1) — the case where audio is a separate downloadable stream rather than muxed into each video
/// variant. Only entries that declare a <c>URI</c> are downloadable and so ever surface here; a group with
/// no <c>URI</c> describes audio that is already embedded in the video segments themselves.
/// </summary>
/// <param name="Uri">The absolute URI of the audio rendition's media playlist.</param>
/// <param name="GroupId">The <c>GROUP-ID</c> a <c>#EXT-X-STREAM-INF</c>'s own <c>AUDIO</c> attribute references.</param>
/// <param name="Name">The <c>NAME</c> attribute, if present.</param>
/// <param name="Language">The <c>LANGUAGE</c> attribute, if present.</param>
/// <param name="IsDefault">Whether <c>DEFAULT=YES</c> was set.</param>
public sealed record HlsAudioRendition(Uri Uri, string GroupId, string? Name, string? Language, bool IsDefault);

/// <summary>
/// A parsed HLS master playlist (TASK-037): the selectable <see cref="Variants"/> and any alternate
/// <see cref="AudioRenditions"/> (empty when audio is muxed into the video streams, the common case).
/// </summary>
/// <param name="Variants">The variant streams in playlist order.</param>
/// <param name="AudioRenditions">The alternate audio renditions with a downloadable <c>URI</c>, in playlist order.</param>
public sealed record HlsMasterPlaylist(
    IReadOnlyList<HlsVariant> Variants, IReadOnlyList<HlsAudioRendition> AudioRenditions);
