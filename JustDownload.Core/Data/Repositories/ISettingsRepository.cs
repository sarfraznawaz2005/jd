namespace JustDownload.Core.Data.Repositories;

/// <summary>
/// Persistence for the key/value <c>settings</c> table (PRD §4.4). A setting is created and updated
/// through the same idempotent <see cref="SetAsync"/> upsert. All access is async and centralized in
/// the data layer (architecture §6). Secrets are never stored here — they live in the OS keychain.
/// </summary>
public interface ISettingsRepository
{
    /// <summary>Gets a setting value by key, or <see langword="null"/> if the key is absent.</summary>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Gets every setting as a key → value map.</summary>
    Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts or updates a setting (upsert) so callers never branch on existence.</summary>
    Task SetAsync(string key, string? value, CancellationToken cancellationToken = default);

    /// <summary>Deletes a setting by key. Returns <see langword="true"/> when a row was removed.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
}
