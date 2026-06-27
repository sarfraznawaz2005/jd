namespace JustDownload.Core.Media;

/// <summary>
/// Runs ffmpeg as a child process, reporting progress and guaranteeing the process is terminated cleanly
/// (TASK-040). Higher-level media tasks (HLS concat, A/V mux, ts→mp4 remux — TASK-041/042) build on this.
/// </summary>
public interface IFfmpegRunner
{
    /// <summary>
    /// Runs ffmpeg with the given <paramref name="arguments"/> (the runner adds banner/log/progress
    /// flags itself), reports parsed <paramref name="progress"/>, and returns the result once it exits.
    /// Cancelling kills the process (and its children) promptly; on any path the process is not left
    /// orphaned.
    /// </summary>
    /// <param name="arguments">The ffmpeg arguments (inputs, filters, output, etc.).</param>
    /// <param name="progress">Optional sink for progress snapshots.</param>
    /// <param name="cancellationToken">Cancels (and kills) the run.</param>
    /// <exception cref="FfmpegException">ffmpeg could not be located or started.</exception>
    Task<FfmpegRunResult> RunAsync(
        IReadOnlyList<string> arguments,
        IProgress<FfmpegProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
