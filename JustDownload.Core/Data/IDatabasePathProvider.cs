namespace JustDownload.Core.Data;

/// <summary>
/// Resolves the on-disk location of the SQLite database. Kept behind an interface so the
/// per-OS resolution is a single, mockable seam — tests substitute a temp path, and the
/// engine never hard-codes a directory (architecture §6 "centralize DB access").
/// </summary>
public interface IDatabasePathProvider
{
    /// <summary>
    /// The directory that holds the database (and its WAL/SHM side-files), under the per-OS
    /// application-data directory in a "JustDownload" subfolder.
    /// </summary>
    string DatabaseDirectory { get; }

    /// <summary>The full path to the SQLite database file.</summary>
    string DatabasePath { get; }
}
