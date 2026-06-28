using JustDownload.Core.Data.Models;
using Microsoft.Data.Sqlite;

namespace JustDownload.Core.Data.Repositories;

/// <summary>
/// Default <see cref="ISegmentRepository"/> over <c>Microsoft.Data.Sqlite</c>. The reserved-word
/// columns <c>"index"</c>, <c>"start"</c> and <c>"end"</c> are quoted to match the schema. Fully
/// parameterized; connections come from the shared WAL factory (CLAUDE.md §6).
/// </summary>
internal sealed class SegmentRepository : ISegmentRepository
{
    private const string Columns =
        "id, download_id, \"index\", \"start\", \"end\", downloaded, state";

    private readonly IDbConnectionFactory _connectionFactory;

    public SegmentRepository(IDbConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<long> AddAsync(DownloadSegment segment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segment);

        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO segments (download_id, "index", "start", "end", downloaded, state)
            VALUES ($download_id, $index, $start, $end, $downloaded, $state);
            SELECT last_insert_rowid();
            """;
        BindWritableColumns(command, segment);

        object? id = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(id, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<DownloadSegment?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM segments WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return Map(reader);
    }

    public async Task<IReadOnlyList<DownloadSegment>> GetByDownloadAsync(
        long downloadId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {Columns} FROM segments WHERE download_id = $download_id ORDER BY \"index\" ASC;";
        command.Parameters.AddWithValue("$download_id", downloadId);

        var results = new List<DownloadSegment>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<bool> UpdateAsync(DownloadSegment segment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segment);

        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE segments SET
                download_id = $download_id, "index" = $index, "start" = $start, "end" = $end,
                downloaded = $downloaded, state = $state
            WHERE id = $id;
            """;
        BindWritableColumns(command, segment);
        command.Parameters.AddWithValue("$id", segment.Id);

        int rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM segments WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        int rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<int> DeleteByDownloadAsync(long downloadId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM segments WHERE download_id = $download_id;";
        command.Parameters.AddWithValue("$download_id", downloadId);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindWritableColumns(SqliteCommand command, DownloadSegment segment)
    {
        command.Parameters.AddWithValue("$download_id", segment.DownloadId);
        command.Parameters.AddWithValue("$index", segment.Index);
        command.Parameters.AddWithValue("$start", segment.Start);
        command.Parameters.AddWithValue("$end", segment.End);
        command.Parameters.AddWithValue("$downloaded", segment.Downloaded);
        command.Parameters.AddWithValue("$state", segment.State);
    }

    private static DownloadSegment Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        DownloadId = reader.GetInt64(1),
        Index = reader.GetInt32(2),
        Start = reader.GetInt64(3),
        End = reader.GetInt64(4),
        Downloaded = reader.GetInt64(5),
        State = reader.GetString(6),
    };
}
