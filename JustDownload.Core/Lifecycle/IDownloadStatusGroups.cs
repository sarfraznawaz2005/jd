namespace JustDownload.Core.Lifecycle;

/// <summary>
/// A live view of which downloads fall into the Completed vs Incomplete buckets (TASK-045, US-8 AC1). It
/// seeds from the persisted downloads and then updates itself from the manager's status events, raising
/// <see cref="Changed"/> whenever a download moves between buckets so the UI's grouped list and sidebar
/// counts stay current without polling.
/// </summary>
public interface IDownloadStatusGroups
{
    /// <summary>Raised whenever group membership changes (a download added, removed, or moved bucket).</summary>
    event EventHandler? Changed;

    /// <summary>The ids of downloads currently in the given group.</summary>
    IReadOnlyList<long> Ids(DownloadStatusGroup group);

    /// <summary>The number of downloads currently in the given group.</summary>
    int Count(DownloadStatusGroup group);

    /// <summary>
    /// (Re)loads the current grouping from persisted downloads. Call once after startup; live status events
    /// keep it current thereafter.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
