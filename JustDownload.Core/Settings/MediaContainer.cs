namespace JustDownload.Core.Settings;

/// <summary>
/// The preferred default container for muxed media output (PRD §4.2 post-processing). Stream-copy
/// into this container is the default; <see cref="Mkv"/> is chosen as the product default because it
/// losslessly holds nearly any codec combination.
/// </summary>
public enum MediaContainer
{
    /// <summary>Matroska (.mkv) — the default; widest codec tolerance.</summary>
    Mkv = 0,

    /// <summary>MPEG-4 (.mp4) — broadest device compatibility.</summary>
    Mp4 = 1,

    /// <summary>WebM (.webm) — VP8/VP9/AV1 + Opus/Vorbis.</summary>
    Webm = 2,
}
