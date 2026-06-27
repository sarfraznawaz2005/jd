namespace JustDownload.Core.Media;

/// <summary>
/// Locates the ffmpeg executable and confirms it runs by reading its version (TASK-040). Resolution
/// order is the configured path, then a bundled vendor directory, then the system <c>PATH</c>.
/// </summary>
public interface IFfmpegLocator
{
    /// <summary>
    /// Returns the located ffmpeg (path + version), or <see langword="null"/> when no working ffmpeg is
    /// found. The result is cached after the first successful resolution.
    /// </summary>
    Task<FfmpegInfo?> LocateAsync(CancellationToken cancellationToken = default);
}
