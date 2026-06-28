namespace JustDownload.Core.Lifecycle;

/// <summary>
/// The download queue (TASK-072, US-16): starts queued downloads through the <see cref="IDownloadManager"/>
/// while honouring the <c>MaxConcurrentDownloads</c> setting and queue <see cref="Data.Models.Download.Priority"/>
/// order (highest first, then oldest). When an active download finishes (or is paused) a freed slot is filled
/// by the next-highest-priority queued download. Drag-to-reorder is persisted via <see cref="ReorderAsync"/>
/// so the order survives a restart. The scheduler (TASK-073) drives this queue's start/pause.
/// </summary>
public interface IDownloadQueueService
{
    /// <summary>The ids the queue is currently running, at most <c>MaxConcurrentDownloads</c> of them.</summary>
    IReadOnlyCollection<long> RunningIds { get; }

    /// <summary>
    /// Enables the queue and starts as many queued downloads as the concurrency limit allows. Newly enqueued
    /// downloads are then auto-started as slots free up until <see cref="StopAsync"/> is called.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables auto-starting and pauses every running download (each transitions to
    /// <see cref="DownloadStatus.Paused"/> and keeps its resume checkpoint). Used by the scheduler's stop.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets one download's priority and re-pumps the queue. Persisted across restarts.</summary>
    Task SetPriorityAsync(long id, int priority, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reorders the queue: <paramref name="orderedIdsHighestFirst"/> get strictly descending priorities so the
    /// first id runs soonest. Persisted, then the queue re-pumps in the new order (TASK-072 AC1).
    /// </summary>
    Task ReorderAsync(IReadOnlyList<long> orderedIdsHighestFirst, CancellationToken cancellationToken = default);
}
