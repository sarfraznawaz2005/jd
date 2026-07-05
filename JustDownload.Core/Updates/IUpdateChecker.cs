namespace JustDownload.Core.Updates;

/// <summary>Checks GitHub Releases for a newer, verified JustDownload build (TASK-080, opt-in, PRD 6.3).</summary>
public interface IUpdateChecker
{
    /// <summary>
    /// Runs one update check: detects a newer release and verifies its <c>checksums.txt</c> signature, but
    /// does not download the installer or launch anything — see <see cref="DownloadAndApplyAsync"/> for
    /// that, run only once the user confirms. Makes no network call at all when auto-update is disabled in
    /// Settings (AC2) or the production signing key isn't configured yet (fail-closed).
    /// </summary>
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the installer described by <paramref name="checkResult"/> (must have
    /// <see cref="UpdateCheckResult.Status"/> == <see cref="UpdateCheckStatus.Available"/>), reporting
    /// fractional progress (0-1) as bytes arrive when the response declares its length, verifies its
    /// SHA-256 against <see cref="UpdateCheckResult.ExpectedSha256"/>, and only then launches it.
    /// </summary>
    Task<UpdateCheckResult> DownloadAndApplyAsync(
        UpdateCheckResult checkResult, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>The most recent result from either <see cref="CheckAsync"/> or <see cref="DownloadAndApplyAsync"/>, if any.</summary>
    UpdateCheckResult? LastResult { get; }
}
