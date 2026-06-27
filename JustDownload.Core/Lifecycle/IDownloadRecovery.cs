namespace JustDownload.Core.Lifecycle;

/// <summary>
/// The startup crash-recovery scan (TASK-029, US-2). A clean exit leaves every download in a terminal or
/// paused state; a download still marked <see cref="DownloadStatus.Active"/> at startup was therefore
/// interrupted by a kill or power loss. Recovery moves those back to <see cref="DownloadStatus.Paused"/> —
/// their per-segment checkpoint is intact — so the UI can offer to resume them.
/// </summary>
public interface IDownloadRecovery
{
    /// <summary>
    /// Scans for downloads left active by an unclean shutdown and marks each resumable (Paused), returning
    /// the affected ids. Idempotent: a second run finds nothing to recover.
    /// </summary>
    Task<IReadOnlyList<long>> RecoverInterruptedAsync(CancellationToken cancellationToken = default);
}
