namespace JustDownload.Core.Updates;

/// <summary>Checks GitHub Releases for a newer, verified JustDownload build (TASK-080, opt-in, PRD 6.3).</summary>
public interface IUpdateChecker
{
    /// <summary>
    /// Runs one update check. Makes no network call at all when auto-update is disabled in Settings (AC2)
    /// or the production signing key isn't configured yet (fail-closed).
    /// </summary>
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);
}
