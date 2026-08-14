namespace JustDownload.Core.Media;

/// <summary>
/// Locates the Deno executable and confirms it runs by reading its version. Resolution order is the
/// configured path, then the downloaded vendor directory, then the system <c>PATH</c>.
/// </summary>
public interface IDenoLocator
{
    /// <summary>
    /// Returns the located Deno (path + version), or <see langword="null"/> when no working Deno is found.
    /// Running <c>deno --version</c> successfully is itself the self-validation. The result is cached after
    /// the first successful resolution.
    /// </summary>
    Task<DenoInfo?> LocateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the cached result so the next <see cref="LocateAsync"/> call re-probes the filesystem/PATH
    /// instead of returning a stale cached entry. Used by <see cref="IDenoProvisioner"/> when it replaces
    /// the vendor-directory binary with a newer pinned version.
    /// </summary>
    void Invalidate();
}
