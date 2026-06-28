using JustDownload.Core.Data.Models;

namespace JustDownload.Core.Data.Repositories;

/// <summary>
/// Persistence for <see cref="DownloadSegment"/> rows over the <c>segments</c> table. Segments are
/// the resume checkpoint, so <see cref="UpdateAsync"/> (bumping <c>downloaded</c>/<c>state</c>) is on
/// the hot checkpoint path. All access is async and centralized in the data layer (architecture §6).
/// </summary>
public interface ISegmentRepository
{
    /// <summary>Inserts a new segment and returns its generated id.</summary>
    Task<long> AddAsync(DownloadSegment segment, CancellationToken cancellationToken = default);

    /// <summary>Gets a segment by id, or <see langword="null"/> if none exists.</summary>
    Task<DownloadSegment?> GetAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Gets all segments for a download, ordered by their index.</summary>
    Task<IReadOnlyList<DownloadSegment>> GetByDownloadAsync(
        long downloadId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a segment's mutable columns (range, downloaded bytes, state). Returns
    /// <see langword="true"/> when a row matched the id.
    /// </summary>
    Task<bool> UpdateAsync(DownloadSegment segment, CancellationToken cancellationToken = default);

    /// <summary>Deletes a segment by id. Returns <see langword="true"/> when a row was removed.</summary>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Deletes every segment for a download in one statement. Returns the number removed.</summary>
    Task<int> DeleteByDownloadAsync(long downloadId, CancellationToken cancellationToken = default);
}
