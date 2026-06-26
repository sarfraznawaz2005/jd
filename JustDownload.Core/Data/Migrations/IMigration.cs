namespace JustDownload.Core.Data.Migrations;

/// <summary>
/// A single, ordered, forward-only schema change. Migrations are <b>versioned and type-safe</b>
/// (architecture §6): each owns a monotonically increasing <see cref="Version"/> and the SQL that
/// brings the database from <c>Version - 1</c> up to <see cref="Version"/>. The
/// <see cref="IMigrationRunner"/> applies pending migrations in version order inside a transaction
/// and records the new version, so re-running is idempotent and a crash mid-migration rolls back
/// cleanly (the durability contract the resume feature relies on, CLAUDE.md §5).
/// </summary>
internal interface IMigration
{
    /// <summary>
    /// The schema version this migration produces. Must be a strictly positive, unique value;
    /// migrations are applied in ascending order and only when greater than the database's current
    /// <c>PRAGMA user_version</c>.
    /// </summary>
    int Version { get; }

    /// <summary>A short human-readable summary, surfaced in logs.</summary>
    string Description { get; }

    /// <summary>
    /// The DDL/DML that performs the upgrade. May contain multiple statements; it is executed as a
    /// single batch within the runner's transaction. It must <b>not</b> open its own transaction or
    /// alter <c>PRAGMA user_version</c> — the runner owns both.
    /// </summary>
    string Sql { get; }
}
