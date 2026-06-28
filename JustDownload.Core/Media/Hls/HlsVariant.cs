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

/// <summary>A parsed HLS master playlist (TASK-037): the selectable <see cref="Variants"/>.</summary>
/// <param name="Variants">The variant streams in playlist order.</param>
public sealed record HlsMasterPlaylist(IReadOnlyList<HlsVariant> Variants);
