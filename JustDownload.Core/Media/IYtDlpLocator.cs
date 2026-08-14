namespace JustDownload.Core.Media;

/// <summary>
/// Locates the yt-dlp executable and confirms it runs by reading its version (TASK-162, D3). Resolution
/// order is the configured path, then the downloaded vendor directory, then the system <c>PATH</c>.
/// </summary>
public interface IYtDlpLocator
{
    /// <summary>
    /// Returns the located yt-dlp (path + version), or <see langword="null"/> when no working yt-dlp is
    /// found. Running <c>yt-dlp --version</c> successfully is itself the self-validation. The result is
    /// cached after the first successful resolution.
    /// </summary>
    Task<YtDlpInfo?> LocateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the cached result so the next <see cref="LocateAsync"/> call re-probes the filesystem/PATH
    /// instead of returning a stale cached entry. Used by <see cref="IYtDlpProvisioner"/> when it replaces
    /// the vendor-directory binary with a newer pinned version.
    /// </summary>
    void Invalidate();
}
