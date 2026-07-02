namespace JustDownload.Core.Media;

/// <summary>
/// Ensures a working yt-dlp is available, downloading the pinned release on first use when the system has
/// none (TASK-162, locked decision D3). Only ever called once the user has explicitly enabled video
/// capture/detection in Settings — nothing yt-dlp-related is fetched or invoked before that.
/// </summary>
public interface IYtDlpProvisioner
{
    /// <summary>
    /// Returns a usable yt-dlp, acquiring one if necessary. If yt-dlp is already on the configured path, the
    /// vendor directory, or <c>PATH</c>, that is returned unchanged. Otherwise, when a pinned build exists
    /// for the current platform, it is downloaded, integrity-checked, and self-validated by running
    /// <c>yt-dlp --version</c>. Returns <see langword="null"/> when no yt-dlp is available and none can be
    /// provisioned for this platform.
    /// </summary>
    /// <exception cref="YtDlpException">
    /// The download failed its integrity check, or the downloaded binary could not be located/run afterwards.
    /// </exception>
    Task<YtDlpInfo?> EnsureAsync(CancellationToken cancellationToken = default);
}
