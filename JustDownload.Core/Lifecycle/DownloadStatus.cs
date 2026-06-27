namespace JustDownload.Core.Lifecycle;

/// <summary>
/// The lifecycle state of a download (TASK-031, PRD US-15b). These are the engine's canonical states;
/// the persisted <see cref="Data.Models.Download.Status"/> string is the stable code for each
/// (see <see cref="DownloadStatusCodes"/>), so the on-disk vocabulary survives even if this enum grows.
/// </summary>
public enum DownloadStatus
{
    /// <summary>Created and waiting to start (in the queue), no bytes fetched yet.</summary>
    Queued,

    /// <summary>Actively downloading on one or more connections.</summary>
    Active,

    /// <summary>Paused by the user; partial data and per-segment offsets are checkpointed for resume.</summary>
    Paused,

    /// <summary>Finished successfully — the file is complete and verified.</summary>
    Completed,

    /// <summary>Stopped by an error (network, disk, server). Recoverable via retry.</summary>
    Failed,

    /// <summary>The source URL has expired (signed-URL/time-limited link). Recoverable via renew.</summary>
    Expired,
}
