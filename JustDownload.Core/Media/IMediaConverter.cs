using JustDownload.Core.Settings;

namespace JustDownload.Core.Media;

/// <summary>
/// Remuxes a downloaded media file into the chosen container by stream copy — no re-encoding, so it is
/// fast and lossless (TASK-042, US-10). The common case is <c>.ts → .mp4</c>. On failure the source file
/// is always left intact.
/// </summary>
public interface IMediaConverter
{
    /// <summary>
    /// Remuxes <paramref name="inputPath"/> into <paramref name="container"/> via stream copy and returns
    /// the output path. The output defaults to the input with the container's extension.
    /// </summary>
    /// <param name="inputPath">The existing media file to remux.</param>
    /// <param name="container">The target container (mp4/mkv).</param>
    /// <param name="outputPath">An explicit output path, or <see langword="null"/> to derive it.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancels the remux (and kills ffmpeg).</param>
    /// <exception cref="FfmpegException">ffmpeg failed; the source is left intact and any partial output removed.</exception>
    Task<string> RemuxAsync(
        string inputPath,
        MediaContainer container,
        string? outputPath = null,
        IProgress<FfmpegProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
