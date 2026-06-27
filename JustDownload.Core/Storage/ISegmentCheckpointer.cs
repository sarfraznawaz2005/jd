using JustDownload.Core.Data.Models;

namespace JustDownload.Core.Storage;

/// <summary>
/// Batches per-segment progress and persists it to SQLite as the resume checkpoint (TASK-025). Callers
/// <see cref="Record"/> the latest state of a segment on every chunk (cheap, in-memory) but only
/// <see cref="FlushAsync"/> writes to the database — periodically (when <see cref="IsFlushDue"/>) and
/// immediately on pause/stop, so the database is not touched per chunk yet a pause loses at most the
/// last interval of progress (which resume simply re-fetches).
/// </summary>
public interface ISegmentCheckpointer
{
    /// <summary>
    /// Records the latest persisted state of a segment (its updated <c>Downloaded</c>/<c>State</c>),
    /// replacing any earlier unflushed record for the same segment. In-memory only.
    /// </summary>
    /// <param name="segment">The segment as it should next be persisted; must have a positive id.</param>
    void Record(DownloadSegment segment);

    /// <summary>Whether there is unflushed progress and the flush interval has elapsed.</summary>
    bool IsFlushDue { get; }

    /// <summary>
    /// Persists all recorded segments to the database, each in a single atomic row update, and resets
    /// the interval. A no-op when nothing is pending. Called on the periodic tick and on pause/stop.
    /// </summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}
