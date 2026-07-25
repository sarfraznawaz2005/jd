namespace JustDownload.Core.Lifecycle;

/// <summary>
/// What an <see cref="DownloadStatus.Active"/> download is currently doing. Fetching bytes is not the only
/// work a download does: a media download still has to join its segments and mux its streams once the last
/// byte is in, and that step moves no bytes at all. Without the distinction the UI shows a frozen byte count
/// under "Downloading" and reads as hung (user-reported).
/// </summary>
public enum DownloadPhase
{
    /// <summary>Fetching bytes from the network — the whole of a plain HTTP download.</summary>
    Transferring,

    /// <summary>Post-processing the fetched data locally (joining segments, muxing streams).</summary>
    Processing,
}
