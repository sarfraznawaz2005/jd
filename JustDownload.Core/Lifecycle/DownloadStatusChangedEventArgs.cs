namespace JustDownload.Core.Lifecycle;

/// <summary>
/// Raised when a download moves between lifecycle states (TASK-031). The UI binds to this to update the
/// list/detail without polling — it carries the download id and both the previous and new status.
/// </summary>
public sealed class DownloadStatusChangedEventArgs : EventArgs
{
    /// <summary>Creates the event payload.</summary>
    public DownloadStatusChangedEventArgs(long downloadId, DownloadStatus? previous, DownloadStatus current)
    {
        DownloadId = downloadId;
        Previous = previous;
        Current = current;
    }

    /// <summary>The affected download's primary key.</summary>
    public long DownloadId { get; }

    /// <summary>The status before the change, or <see langword="null"/> for the initial enqueue.</summary>
    public DownloadStatus? Previous { get; }

    /// <summary>The status after the change.</summary>
    public DownloadStatus Current { get; }
}
