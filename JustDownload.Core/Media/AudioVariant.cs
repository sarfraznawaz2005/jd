namespace JustDownload.Core.Media;

/// <summary>
/// One selectable audio rendition of a separate-streams media item (TASK-039): its identifier, advertised
/// bandwidth and language when known. DASH/HLS extractors produce these alongside the video
/// <see cref="VideoVariant"/>s so an audio track can be chosen for muxing (TASK-041).
/// </summary>
/// <param name="Id">An opaque identifier (e.g. the audio stream/representation URL).</param>
/// <param name="Bandwidth">The advertised bits-per-second, if known.</param>
/// <param name="Language">The BCP-47 / ISO language tag, if declared.</param>
/// <param name="LanguagePreference">yt-dlp's raw <c>language_preference</c> signal, if declared: a relative
/// ranking where the original/default-language audio track carries the highest value among a video's audio
/// formats, with dubs/auto-translations ranked lower. Only yt-dlp populates this; DASH/HLS/Twitter extractors
/// leave it <see langword="null"/>.</param>
public sealed record AudioVariant(
    string Id, long? Bandwidth = null, string? Language = null, int? LanguagePreference = null);
