using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Data;

/// <summary>
/// Default <see cref="IDbConnectionFactory"/> over <c>Microsoft.Data.Sqlite</c>.
/// <para>
/// Enables <b>WAL</b> (write-ahead logging) so readers never block the single writer and a crash
/// can never tear a half-written transaction — the durability contract the resume feature relies
/// on (CLAUDE.md §5). Each connection also gets a <c>busy_timeout</c> so concurrent writers wait
/// for a lock instead of failing with <c>SQLITE_BUSY</c>, and <c>foreign_keys=ON</c> to enforce
/// referential integrity (segments → downloads, etc.).
/// </para>
/// </summary>
internal sealed partial class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;
    private readonly int _busyTimeoutMs;
    private readonly ILogger<SqliteConnectionFactory> _logger;

    public SqliteConnectionFactory(
        IDatabasePathProvider pathProvider,
        DatabaseOptions options,
        ILogger<SqliteConnectionFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(pathProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _busyTimeoutMs = (int)Math.Clamp(
            options.BusyTimeout.TotalMilliseconds, 0, int.MaxValue);

        // The OS app-data root exists already; ensure our per-app subfolder is present so the very
        // first open can create the database file (and its -wal/-shm side-files) there.
        Directory.CreateDirectory(pathProvider.DatabaseDirectory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = pathProvider.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Private cache (the default) is required for correct WAL concurrency across
            // connections; shared-cache would serialise readers against the writer.
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ConnectionString;
    }

    public SqliteConnection CreateOpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            ApplyPragmas(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public async Task<SqliteConnection> CreateOpenConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            ApplyPragmas(connection);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void ApplyPragmas(SqliteConnection connection)
    {
        // journal_mode=WAL is a persistent, database-level setting; re-issuing it per open is
        // idempotent and cheap, and guards the case where the file was just created. busy_timeout
        // and foreign_keys are per-connection and must be set every open.
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = string.Format(
            CultureInfo.InvariantCulture,
            "PRAGMA journal_mode=WAL; PRAGMA busy_timeout={0}; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;",
            _busyTimeoutMs);
        command.ExecuteNonQuery();

        LogConnectionOpened(_logger, _busyTimeoutMs);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "Opened SQLite connection (WAL, busy_timeout={BusyTimeoutMs}ms).")]
    private static partial void LogConnectionOpened(ILogger logger, int busyTimeoutMs);
}
