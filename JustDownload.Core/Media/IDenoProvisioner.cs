namespace JustDownload.Core.Media;

/// <summary>
/// Ensures a working Deno is available, downloading the pinned build on first use when the system has none.
/// Deno is the JS-runtime yt-dlp needs to solve YouTube's signature/JS challenges (see
/// <see cref="DenoManifest"/>); provisioning it is wired into the existing "Download yt-dlp" action
/// (D3: provisioning is always an explicit user action, never implicit) rather than a separate button.
/// </summary>
public interface IDenoProvisioner
{
    /// <summary>
    /// Returns a usable Deno, acquiring one if necessary. If Deno is already on the configured path, the
    /// vendor directory, or <c>PATH</c>, that is returned unchanged. Otherwise, when a pinned build exists
    /// for the current platform, it is downloaded, integrity-checked, and extracted. Returns
    /// <see langword="null"/> when no Deno is available and none can be provisioned for this platform —
    /// callers should degrade gracefully (yt-dlp still works without Deno, just without a JS runtime).
    /// </summary>
    /// <exception cref="DenoException">
    /// The download failed its integrity check, or the extracted build could not be located/run afterwards.
    /// </exception>
    Task<DenoInfo?> EnsureAsync(CancellationToken cancellationToken = default);
}
