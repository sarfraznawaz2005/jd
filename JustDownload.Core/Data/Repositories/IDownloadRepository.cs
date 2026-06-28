using JustDownload.Core.Data.Models;

namespace JustDownload.Core.Data.Repositories;

/// <summary>
/// Persistence for <see cref="Download"/> rows — the centralized data-access seam over the
/// <c>downloads</c> table (architecture §6 "centralize DB access"; no SQL lives outside this layer).
/// All operations are async and honor a <see cref="CancellationToken"/> (CLAUDE.md §1).
/// </summary>
public interface IDownloadRepository
{
    /// <summary>Inserts a new download and returns its generated id.</summary>
    Task<long> AddAsync(Download download, CancellationToken cancellationToken = default);

    /// <summary>Gets a download by id, or <see langword="null"/> if none exists.</summary>
    Task<Download?> GetAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Gets every download, newest first.</summary>
    Task<IReadOnlyList<Download>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates all mutable columns of an existing download. Returns <see langword="true"/> when a
    /// row matched the id, <see langword="false"/> otherwise.
    /// </summary>
    Task<bool> UpdateAsync(Download download, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a download by id (cascading to its segments and auth row). Returns
    /// <see langword="true"/> when a row was removed.
    /// </summary>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets downloads in the given lifecycle <paramref name="statusCode"/>, ordered by queue priority
    /// (highest first, then oldest first) — the order the queue starts them in (TASK-072, US-16).
    /// </summary>
    Task<IReadOnlyList<Download>> GetByStatusOrderedByPriorityAsync(
        string statusCode, CancellationToken cancellationToken = default);

    /// <summary>Sets just the queue <c>priority</c> for a download (TASK-072). Returns whether a row matched.</summary>
    Task<bool> SetPriorityAsync(long id, int priority, CancellationToken cancellationToken = default);
}
