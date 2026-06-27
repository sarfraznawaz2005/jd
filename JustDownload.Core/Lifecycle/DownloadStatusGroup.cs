namespace JustDownload.Core.Lifecycle;

/// <summary>
/// The top-level status grouping the UI organizes the download list by (TASK-045, PRD US-8 AC1): everything
/// is either finished or not. This is deliberately coarser than <see cref="DownloadStatus"/> — the sidebar
/// shows just "Completed" and "Incomplete" buckets, while the fine-grained state drives the row's badge.
/// </summary>
public enum DownloadStatusGroup
{
    /// <summary>In-progress, queued, paused, failed, or expired — anything not finished.</summary>
    Incomplete,

    /// <summary>Finished successfully.</summary>
    Completed,
}

/// <summary>
/// The single source of truth mapping a fine-grained <see cref="DownloadStatus"/> to its
/// <see cref="DownloadStatusGroup"/> (TASK-045). Both the auto-organizer's folder rules and the UI's status
/// grouping resolve through this, so "what counts as complete" is defined in exactly one place.
/// </summary>
public static class DownloadStatusGroups
{
    /// <summary>The group a status belongs to: only <see cref="DownloadStatus.Completed"/> is complete.</summary>
    public static DownloadStatusGroup Of(DownloadStatus status) =>
        status == DownloadStatus.Completed ? DownloadStatusGroup.Completed : DownloadStatusGroup.Incomplete;

    /// <summary>The group for a persisted status code (see <see cref="DownloadStatusCodes"/>).</summary>
    public static DownloadStatusGroup OfCode(string code) => Of(DownloadStatusCodes.Parse(code));
}
