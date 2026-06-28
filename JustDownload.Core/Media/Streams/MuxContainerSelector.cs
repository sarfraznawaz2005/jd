using JustDownload.Core.Settings;

namespace JustDownload.Core.Media.Streams;

/// <summary>
/// Decides the output container for a stream-copy mux (TASK-041 AC1). MKV is the default because it holds
/// virtually any codec combination losslessly; MP4 is chosen only when both codecs are MP4-compatible, so a
/// pure stream copy (no re-encode, AC2) always succeeds. Pure and deterministic — codec strings are matched
/// by their leading token (e.g. <c>avc1.640028</c> → <c>avc1</c>). Unknown codecs are treated as
/// MP4-incompatible, falling back to the safe MKV container.
/// </summary>
public static class MuxContainerSelector
{
    private static readonly HashSet<string> Mp4VideoCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "h264", "avc", "avc1", "avc3", "x264",
        "h265", "hevc", "hev1", "hvc1", "x265",
        "mpeg4", "mp4v", "av1", "av01",
    };

    private static readonly HashSet<string> Mp4AudioCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac", "mp4a", "ac3", "ac-3", "eac3", "ec-3", "mp3", "mp4a.40", "alac",
    };

    /// <summary>
    /// Returns the container to use given the user's <paramref name="preferred"/> default and the (optional)
    /// known codecs. Anything other than an MP4 preference with two MP4-compatible codecs yields MKV.
    /// </summary>
    public static MediaContainer Select(MediaContainer preferred, string? videoCodec, string? audioCodec)
    {
        if (preferred != MediaContainer.Mp4)
        {
            // MKV (default) and WebM-as-MKV both map to the lossless MKV container for stream copy.
            return MediaContainer.Mkv;
        }

        return IsMp4Compatible(videoCodec, Mp4VideoCodecs) && IsMp4Compatible(audioCodec, Mp4AudioCodecs)
            ? MediaContainer.Mp4
            : MediaContainer.Mkv;
    }

    private static bool IsMp4Compatible(string? codec, HashSet<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return false; // Unknown — be safe and prefer MKV.
        }

        string token = codec.Trim();
        int dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            token = token[..dot];
        }

        return allowed.Contains(token);
    }
}
