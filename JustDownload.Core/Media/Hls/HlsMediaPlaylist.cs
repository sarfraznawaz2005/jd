namespace JustDownload.Core.Media.Hls;

/// <summary>
/// A parsed HLS media playlist (TASK-037): the ordered <see cref="Segments"/> to download and concatenate,
/// the <see cref="TargetDuration"/>, the starting <see cref="MediaSequence"/>, and whether the playlist is
/// complete (<see cref="IsEndList"/> — a VOD playlist that will not grow).
/// </summary>
/// <param name="Segments">The segments in exact playlist order (order is load-bearing for concat — TASK-038).</param>
/// <param name="TargetDuration">The maximum segment duration (from <c>#EXT-X-TARGETDURATION</c>).</param>
/// <param name="MediaSequence">The media sequence number of the first segment (from <c>#EXT-X-MEDIA-SEQUENCE</c>, default 0).</param>
/// <param name="IsEndList">Whether <c>#EXT-X-ENDLIST</c> was present (a finished VOD playlist).</param>
public sealed record HlsMediaPlaylist(
    IReadOnlyList<HlsSegment> Segments,
    int TargetDuration,
    long MediaSequence,
    bool IsEndList);
