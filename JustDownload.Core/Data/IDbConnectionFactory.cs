using Microsoft.Data.Sqlite;

namespace JustDownload.Core.Data;

/// <summary>
/// Creates ready-to-use SQLite connections for the engine. Every connection it hands back is
/// already open and configured for safe concurrent access (WAL journaling + a busy timeout), so
/// callers never repeat the pragma plumbing. This is the single chokepoint for opening the
/// database (architecture §6 "centralize DB access").
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>
    /// Opens a new connection to the application database and applies the engine's pragmas
    /// (WAL journal mode, busy timeout, foreign keys). The caller owns the returned connection
    /// and must dispose it.
    /// </summary>
    SqliteConnection CreateOpenConnection();

    /// <summary>
    /// Asynchronously opens a new connection and applies the engine's pragmas. The caller owns
    /// the returned connection and must dispose it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the open/pragma round-trip.</param>
    Task<SqliteConnection> CreateOpenConnectionAsync(CancellationToken cancellationToken = default);
}
