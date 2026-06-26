using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Data.Migrations;

/// <summary>
/// Default <see cref="IMigrationRunner"/>. Reads the database's <c>PRAGMA user_version</c>, then
/// applies each registered <see cref="IMigration"/> whose version is greater, in ascending order.
/// Every migration's SQL and its version bump run in a <b>single transaction</b>, so the schema and
/// the recorded version can never drift apart even if the process is killed mid-run (CLAUDE.md §5).
/// Re-running with no pending migrations is a cheap no-op, which keeps app startup idempotent.
/// </summary>
internal sealed partial class MigrationRunner : IMigrationRunner
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<MigrationRunner> _logger;
    private readonly IMigration[] _migrations;

    public MigrationRunner(
        IDbConnectionFactory connectionFactory,
        IEnumerable<IMigration> migrations,
        ILogger<MigrationRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentNullException.ThrowIfNull(logger);

        _connectionFactory = connectionFactory;
        _logger = logger;
        _migrations = Order(migrations);
    }

    public int Migrate()
    {
        using SqliteConnection connection = _connectionFactory.CreateOpenConnection();

        int current = ReadUserVersion(connection);
        foreach (IMigration migration in _migrations)
        {
            if (migration.Version <= current)
            {
                continue;
            }

            using SqliteTransaction transaction = connection.BeginTransaction();
            Execute(connection, transaction, migration.Sql);
            SetUserVersion(connection, transaction, migration.Version);
            transaction.Commit();

            current = migration.Version;
            LogApplied(_logger, migration.Version, migration.Description);
        }

        LogUpToDate(_logger, current);
        return current;
    }

    public async Task<int> MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        int current = await ReadUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        foreach (IMigration migration in _migrations)
        {
            if (migration.Version <= current)
            {
                continue;
            }

            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, migration.Sql, cancellationToken).ConfigureAwait(false);
            await SetUserVersionAsync(connection, transaction, migration.Version, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            current = migration.Version;
            LogApplied(_logger, migration.Version, migration.Description);
        }

        LogUpToDate(_logger, current);
        return current;
    }

    /// <summary>
    /// Sorts migrations by version and validates the set: every version must be strictly positive
    /// and unique. A duplicate or non-positive version is a programming error (illegal state) and
    /// fails loudly rather than silently corrupting the version sequence.
    /// </summary>
    private static IMigration[] Order(IEnumerable<IMigration> migrations)
    {
        IMigration[] ordered = migrations.OrderBy(m => m.Version).ToArray();

        for (int i = 0; i < ordered.Length; i++)
        {
            if (ordered[i].Version <= 0)
            {
                throw new InvalidOperationException(
                    $"Migration '{ordered[i].Description}' has non-positive version {ordered[i].Version}; versions must be >= 1.");
            }

            if (i > 0 && ordered[i].Version == ordered[i - 1].Version)
            {
                throw new InvalidOperationException(
                    $"Duplicate migration version {ordered[i].Version} detected; each migration must have a unique version.");
            }
        }

        return ordered;
    }

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static async Task<int> ReadUserVersionAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // PRAGMA statements cannot be parameterised, so the version is interpolated. It is an int we
    // fully control (validated in Order), never user input, so there is no injection surface.
    private static void SetUserVersion(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = string.Format(CultureInfo.InvariantCulture, "PRAGMA user_version = {0};", version);
        command.ExecuteNonQuery();
    }

    private static async Task SetUserVersionAsync(
        SqliteConnection connection, SqliteTransaction transaction, int version, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = string.Format(CultureInfo.InvariantCulture, "PRAGMA user_version = {0};", version);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Applied schema migration v{Version}: {Description}")]
    private static partial void LogApplied(ILogger logger, int version, string description);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Database schema is up to date at version {Version}.")]
    private static partial void LogUpToDate(ILogger logger, int version);
}
