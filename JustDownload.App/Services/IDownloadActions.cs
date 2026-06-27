namespace JustDownload.App.Services;

/// <summary>
/// The UI-facing action surface for operating on a single download by id (TASK-051 context menu; reused by
/// the TASK-052 toolbar). It sits over <see cref="JustDownload.Core.Lifecycle.IDownloadManager"/> and owns the
/// per-download cancellation handles a running transfer needs, so the engine stays unaware of the UI session.
/// All methods are safe to call regardless of the download's current state — an inapplicable call is a no-op.
/// </summary>
public interface IDownloadActions
{
    /// <summary>
    /// Starts or resumes the download in the background, tracking its cancellation handle so it can later be
    /// paused. No-op if it is already running. Errors are handled by the engine (transition to Failed) and the
    /// global error handler; this method never throws for a download-level failure.
    /// </summary>
    void Start(long id);

    /// <summary>Pauses a running download by cancelling its transfer; no-op if it is not running.</summary>
    void Pause(long id);

    /// <summary>Whether a transfer for the given download is currently active in this session.</summary>
    bool IsRunning(long id);

    /// <summary>Removes the download record (and its segments/auth) from the store. Leaves any file on disk.</summary>
    Task RemoveAsync(long id, CancellationToken cancellationToken = default);
}
