namespace JustDownload.Core.Updates;

/// <summary>The result of an <see cref="IUpdateChecker.CheckAsync"/> call (TASK-080).</summary>
/// <param name="InstallerAssetName">
/// The verified installer's asset name, populated only when <paramref name="Status"/> is
/// <see cref="UpdateCheckStatus.Available"/> — carried into <see cref="IUpdateChecker.DownloadAndApplyAsync"/>.
/// </param>
/// <param name="InstallerDownloadUrl">The verified installer's download URL, populated alongside <paramref name="InstallerAssetName"/>.</param>
/// <param name="ExpectedSha256">The installer's signed SHA-256 hash, populated alongside <paramref name="InstallerAssetName"/>.</param>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string? LatestVersion = null,
    Uri? ReleaseUrl = null,
    string? ErrorMessage = null,
    string? InstallerAssetName = null,
    Uri? InstallerDownloadUrl = null,
    string? ExpectedSha256 = null);
