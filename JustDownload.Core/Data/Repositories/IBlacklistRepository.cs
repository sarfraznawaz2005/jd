using JustDownload.Core.Data.Models;

namespace JustDownload.Core.Data.Repositories;

/// <summary>
/// Persistence for the <c>site_blacklist</c> table (PRD §4.4, US-12). Because both columns form the
/// natural key, an entry has no separately-mutable field: "create" is an idempotent
/// <see cref="AddAsync"/> upsert and "delete" removes it. All access is async and centralized in the
/// data layer (architecture §6).
/// </summary>
public interface IBlacklistRepository
{
    /// <summary>
    /// Adds a blacklist entry. Idempotent: re-adding an existing (domain, scope) pair is a no-op
    /// rather than an error.
    /// </summary>
    Task AddAsync(BlacklistEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Returns whether a (domain, scope) pair is blacklisted.</summary>
    Task<bool> ExistsAsync(string domain, string scope, CancellationToken cancellationToken = default);

    /// <summary>Gets every blacklist entry, ordered by domain then scope.</summary>
    Task<IReadOnlyList<BlacklistEntry>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a blacklist entry. Returns <see langword="true"/> when a row was removed.
    /// </summary>
    Task<bool> DeleteAsync(string domain, string scope, CancellationToken cancellationToken = default);
}
