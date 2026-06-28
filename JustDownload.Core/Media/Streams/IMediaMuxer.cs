using JustDownload.Core.Settings;

namespace JustDownload.Core.Media.Streams;

/// <summary>
/// A request to mux a separate video file and audio file into one container (TASK-041).
/// </summary>
public sealed record MuxRequest
{
    /// <summary>The video-only input file.</summary>
    public required string VideoPath { get; init; }

    /// <summary>The audio-only input file.</summary>
    public required string AudioPath { get; init; }

    /// <summary>The preferred container; MKV by default. MP4 is honoured only when both codecs allow it.</summary>
    public MediaContainer PreferredContainer { get; init; } = MediaContainer.Mkv;

    /// <summary>The video codec (e.g. from the extractor's CODECS), used to decide MP4 vs MKV.</summary>
    public string? VideoCodec { get; init; }

    /// <summary>The audio codec, used to decide MP4 vs MKV.</summary>
    public string? AudioCodec { get; init; }

    /// <summary>An explicit output path, or <see langword="null"/> to derive it from the video file + chosen container.</summary>
    public string? OutputPath { get; init; }
}

/// <summary>The result of a mux (TASK-041): the output file and the container actually used.</summary>
/// <param name="OutputPath">The muxed output file.</param>
/// <param name="Container">The container chosen (MKV, or MP4 when codecs allowed).</param>
public sealed record MuxResult(string OutputPath, MediaContainer Container);

/// <summary>
/// Muxes a separate video stream and audio stream into a single playable container by stream copy (TASK-041,
/// US-9b AC3): no re-encoding (AC2), MKV by default and MP4 when the codecs allow (AC1), producing a file a
/// player/ffprobe reads as one valid container (AC0). Built on the <see cref="IFfmpegRunner"/>.
/// </summary>
public interface IMediaMuxer
{
    /// <summary>
    /// Muxes the two streams per <paramref name="request"/> and returns the output path and chosen container.
    /// On failure the partial output is removed and the source files are left intact.
    /// </summary>
    /// <param name="request">The inputs, codec hints, and container preference.</param>
    /// <param name="progress">Optional ffmpeg progress sink.</param>
    /// <param name="cancellationToken">Cancels the mux (and kills ffmpeg).</param>
    /// <exception cref="FfmpegException">ffmpeg failed; the partial output is removed and inputs are kept.</exception>
    Task<MuxResult> MuxAsync(
        MuxRequest request,
        IProgress<FfmpegProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
