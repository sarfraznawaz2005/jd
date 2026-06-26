namespace JustDownload.Core.Data.Migrations;

/// <summary>
/// Applies the engine's versioned schema migrations to the SQLite database. This is the only place
/// schema is created or evolved — the app never mutates the schema ad-hoc at runtime (architecture
/// §6). The applied version is tracked via <c>PRAGMA user_version</c>; calling a migrate method is
/// <b>idempotent</b>: already-applied migrations are skipped and the version stays stable.
/// </summary>
public interface IMigrationRunner
{
    /// <summary>
    /// Brings the database schema up to date, applying every migration whose version exceeds the
    /// current <c>PRAGMA user_version</c> in ascending order. Each migration runs in its own
    /// transaction together with the version bump, so a failure leaves the schema and version
    /// consistent.
    /// </summary>
    /// <returns>The schema version after migrating (the highest migration applied, or the existing
    /// version if none were pending).</returns>
    int Migrate();

    /// <summary>
    /// Asynchronous counterpart of <see cref="Migrate"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the migration round-trip.</param>
    /// <returns>The schema version after migrating.</returns>
    Task<int> MigrateAsync(CancellationToken cancellationToken = default);
}
