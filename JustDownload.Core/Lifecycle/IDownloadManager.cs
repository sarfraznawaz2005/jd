using JustDownload.Core.Downloading;

namespace JustDownload.Core.Lifecycle;

/// <summary>
/// Orchestrates a download across its lifecycle (TASK-031): enqueue → start → complete/fail, persisting the
/// state at every step and raising observable events the UI binds to (US-15b). It is the single seam the App
/// drives downloads through; the segmentation engine underneath stays unaware of queues, persistence, or the
/// UI. Transitions are validated by <see cref="DownloadStateMachine"/>, so illegal moves throw rather than
/// corrupt state.
/// </summary>
public interface IDownloadManager
{
    /// <summary>Raised whenever a download changes lifecycle state (including the initial enqueue).</summary>
    event EventHandler<DownloadStatusChangedEventArgs>? StatusChanged;

    /// <summary>Raised as an active download makes progress, carrying the latest snapshot.</summary>
    event EventHandler<DownloadProgressChangedEventArgs>? ProgressChanged;

    /// <summary>
    /// Creates a queued download, persists it, and returns its id. Raises <see cref="StatusChanged"/> with a
    /// <c>null</c> previous status. Does not start transferring — call <see cref="StartAsync"/> for that.
    /// </summary>
    Task<long> EnqueueAsync(EnqueueDownloadRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts (or resumes/retries) the download with the given id: transitions it to
    /// <see cref="DownloadStatus.Active"/>, runs the transfer, and on completion transitions it to
    /// <see cref="DownloadStatus.Completed"/>. Cancelling <paramref name="cancellationToken"/> pauses it
    /// (transition to <see cref="DownloadStatus.Paused"/>); any other failure transitions it to
    /// <see cref="DownloadStatus.Failed"/>. The new state is persisted and events are raised throughout.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No download exists with that id.</exception>
    /// <exception cref="InvalidOperationException">The download is in a state it cannot be started from.</exception>
    Task<DownloadResult> StartAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews an expired (or failed) download with a fresh <paramref name="newUrl"/> and continues it: if the
    /// new resource is provably the same bytes (matching ETag, else matching size) the existing checkpoint is
    /// kept and the download resumes; otherwise the checkpoint is dropped and it restarts cleanly (TASK-032,
    /// US-13). Returns the completed download's result.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No download exists with that id.</exception>
    /// <exception cref="DownloadExpiredException">The replacement URL is itself expired.</exception>
    Task<DownloadResult> RenewAsync(long id, Uri newUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// The latest in-memory progress snapshot for a download, or <see langword="null"/> if none has been
    /// observed this session (e.g. it has not started since launch).
    /// </summary>
    DownloadProgress? GetProgress(long id);
}
