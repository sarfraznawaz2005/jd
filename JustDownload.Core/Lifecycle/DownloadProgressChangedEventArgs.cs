namespace JustDownload.Core.Lifecycle;

/// <summary>
/// Raised as a download makes progress (TASK-031). Carries the download id and the latest
/// <see cref="DownloadProgress"/> snapshot so the UI can update the progress bar, speed, and ETA live.
/// </summary>
public sealed class DownloadProgressChangedEventArgs : EventArgs
{
    /// <summary>Creates the event payload.</summary>
    public DownloadProgressChangedEventArgs(long downloadId, DownloadProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        DownloadId = downloadId;
        Progress = progress;
    }

    /// <summary>The affected download's primary key.</summary>
    public long DownloadId { get; }

    /// <summary>The latest progress snapshot.</summary>
    public DownloadProgress Progress { get; }
}
