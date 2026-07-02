namespace JustDownload.Core.Updates;

/// <summary>The result of an <see cref="IUpdateChecker.CheckAsync"/> call (TASK-080).</summary>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string? LatestVersion = null,
    Uri? ReleaseUrl = null,
    string? ErrorMessage = null);
