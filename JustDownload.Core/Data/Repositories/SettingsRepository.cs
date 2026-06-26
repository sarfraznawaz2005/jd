using Microsoft.Data.Sqlite;

namespace JustDownload.Core.Data.Repositories;

/// <summary>
/// Default <see cref="ISettingsRepository"/> over <c>Microsoft.Data.Sqlite</c>. Writes use SQLite's
/// <c>ON CONFLICT</c> upsert so create and update share one path. The <c>key</c> column is a
/// reserved word and is quoted. Fully parameterized (CLAUDE.md §6).
/// </summary>
internal sealed class SettingsRepository : ISettingsRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public SettingsRepository(IDbConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE \"key\" = $key;";
        command.Parameters.AddWithValue("$key", key);

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return reader.GetNullableString(0);
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT \"key\", value FROM settings ORDER BY \"key\" ASC;";

        var results = new Dictionary<string, string?>(StringComparer.Ordinal);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results[reader.GetString(0)] = reader.GetNullableString(1);
        }

        return results;
    }

    public async Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO settings ("key", value) VALUES ($key, $value)
            ON CONFLICT ("key") DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM settings WHERE \"key\" = $key;";
        command.Parameters.AddWithValue("$key", key);

        int rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows > 0;
    }
}
