namespace JustDownload.Core.Media;

/// <summary>
/// Ensures an LGPL ffmpeg is available, downloading one on first use when the system has none (TASK-079,
/// D7). It never fetches a GPL build, and verifies every download against a pinned SHA-256 before use.
/// </summary>
public interface IFfmpegProvisioner
{
    /// <summary>
    /// Returns a usable ffmpeg, acquiring one if necessary. If ffmpeg is already on the configured path,
    /// the vendor directory, or <c>PATH</c>, that is returned unchanged. Otherwise, when a pinned LGPL
    /// build exists for the current platform, it is downloaded, integrity-checked, and extracted into the
    /// vendor directory. Returns <see langword="null"/> when no ffmpeg is available and none can be
    /// provisioned for this platform — the caller should then surface a "please install ffmpeg" message.
    /// </summary>
    /// <exception cref="FfmpegException">
    /// The download failed its integrity check, a non-LGPL source was configured, or the extracted build
    /// could not be located afterwards.
    /// </exception>
    Task<FfmpegInfo?> EnsureAsync(CancellationToken cancellationToken = default);
}
