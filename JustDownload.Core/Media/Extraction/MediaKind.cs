namespace JustDownload.Core.Media.Extraction;

/// <summary>
/// What an <see cref="IMediaExtractor"/> determined a URL points at (TASK-036). The kind tells the engine
/// which download path to take: a direct file, an HLS playlist to be segmented and concatenated
/// (TASK-037/038), an adaptive set whose video and audio are separate streams to be downloaded and muxed
/// (TASK-039/041), or a DASH manifest.
/// </summary>
public enum MediaKind
{
    /// <summary>A single directly-downloadable media file (e.g. a progressive <c>.mp4</c>).</summary>
    Progressive = 0,

    /// <summary>An HLS playlist (<c>.m3u8</c>) — master with variants and/or a media playlist of segments.</summary>
    Hls = 1,

    /// <summary>A DASH manifest (<c>.mpd</c>) describing adaptive representations.</summary>
    Dash = 2,

    /// <summary>Two independent streams (separate video + audio) to be downloaded and muxed together.</summary>
    SeparateStreams = 3,
}
