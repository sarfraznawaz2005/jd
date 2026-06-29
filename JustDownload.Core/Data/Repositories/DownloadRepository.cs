using JustDownload.Core.Data.Models;
using Microsoft.Data.Sqlite;

namespace JustDownload.Core.Data.Repositories;

/// <summary>
/// Default <see cref="IDownloadRepository"/> over <c>Microsoft.Data.Sqlite</c>. Every statement is
/// fully parameterized (no string concatenation of values), and reserved-word columns
/// (<c>"index"</c>, etc.) are not touched here. Connections come from the shared factory so WAL and
/// the busy-timeout apply uniformly (CLAUDE.md §6).
/// </summary>
internal sealed class DownloadRepository : IDownloadRepository
{
    private const string Columns =
        "id, url, referrer, filename, dir, total_bytes, status, category_type, category_status, " +
        "etag, last_modified, created_at, completed_at, error, max_connections, speed_limit, priority, " +
        "cookie_secret_ref, retry_count, " +
        "proxy_kind, proxy_host, proxy_port, proxy_username, proxy_domain, proxy_password_secret_ref, " +
        "media_kind, media_audio_url, media_container";

    private readonly IDbConnectionFactory _connectionFactory;

    public DownloadRepository(IDbConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<long> AddAsync(Download download, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(download);

        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO downloads
                (url, referrer, filename, dir, total_bytes, status, category_type, category_status,
                 etag, last_modified, created_at, completed_at, error, max_connections, speed_limit, priority,
                 cookie_secret_ref, retry_count,
                 proxy_kind, proxy_host, proxy_port, proxy_username, proxy_domain, proxy_password_secret_ref,
                 media_kind, media_audio_url, media_container)
            VALUES
                ($url, $referrer, $filename, $dir, $total_bytes, $status, $category_type, $category_status,
                 $etag, $last_modified, $created_at, $completed_at, $error, $max_connections, $speed_limit,
                 $priority, $cookie_secret_ref, $retry_count,
                 $proxy_kind, $proxy_host, $proxy_port, $proxy_username, $proxy_domain, $proxy_password_secret_ref,
                 $media_kind, $media_audio_url, $media_container);
            SELECT last_insert_rowid();
            """;
        BindWritableColumns(command, download);

        object? id = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(id, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<Download?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM downloads WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return Map(reader);
    }

    public async Task<IReadOnlyList<Download>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM downloads ORDER BY created_at DESC, id DESC;";

        var results = new List<Download>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<bool> UpdateAsync(Download download, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(download);

        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE downloads SET
                url = $url, referrer = $referrer, filename = $filename, dir = $dir,
                total_bytes = $total_bytes, status = $status, category_type = $category_type,
                category_status = $category_status, etag = $etag, last_modified = $last_modified,
                created_at = $created_at, completed_at = $completed_at, error = $error,
                max_connections = $max_connections, speed_limit = $speed_limit, priority = $priority,
                cookie_secret_ref = $cookie_secret_ref, retry_count = $retry_count,
                proxy_kind = $proxy_kind, proxy_host = $proxy_host, proxy_port = $proxy_port,
                proxy_username = $proxy_username, proxy_domain = $proxy_domain,
                proxy_password_secret_ref = $proxy_password_secret_ref,
                media_kind = $media_kind, media_audio_url = $media_audio_url, media_container = $media_container
            WHERE id = $id;
            """;
        BindWritableColumns(command, download);
        command.Parameters.AddWithValue("$id", download.Id);

        int rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM downloads WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        int rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows > 0;
    }

    private static void BindWritableColumns(SqliteCommand command, Download download)
    {
        command.Parameters.AddWithValue("$url", download.Url);
        command.Parameters.AddWithValue("$referrer", (object?)download.Referrer ?? DBNull.Value);
        command.Parameters.AddWithValue("$filename", (object?)download.Filename ?? DBNull.Value);
        command.Parameters.AddWithValue("$dir", (object?)download.Directory ?? DBNull.Value);
        command.Parameters.AddWithValue("$total_bytes", (object?)download.TotalBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", download.Status);
        command.Parameters.AddWithValue("$category_type", (object?)download.CategoryType ?? DBNull.Value);
        command.Parameters.AddWithValue("$category_status", (object?)download.CategoryStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$etag", (object?)download.ETag ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_modified", (object?)download.LastModified ?? DBNull.Value);
        command.Parameters.AddWithValue("$created_at", download.CreatedAt.ToStorage());
        command.Parameters.AddWithValue(
            "$completed_at", (object?)download.CompletedAt?.ToStorage() ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)download.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$max_connections", (object?)download.MaxConnections ?? DBNull.Value);
        command.Parameters.AddWithValue("$speed_limit", (object?)download.SpeedLimit ?? DBNull.Value);
        command.Parameters.AddWithValue("$priority", download.Priority);
        command.Parameters.AddWithValue("$cookie_secret_ref", (object?)download.CookieSecretRef ?? DBNull.Value);
        command.Parameters.AddWithValue("$retry_count", download.RetryCount);
        command.Parameters.AddWithValue("$proxy_kind", (object?)download.ProxyKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$proxy_host", (object?)download.ProxyHost ?? DBNull.Value);
        command.Parameters.AddWithValue("$proxy_port", (object?)download.ProxyPort ?? DBNull.Value);
        command.Parameters.AddWithValue("$proxy_username", (object?)download.ProxyUsername ?? DBNull.Value);
        command.Parameters.AddWithValue("$proxy_domain", (object?)download.ProxyDomain ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$proxy_password_secret_ref", (object?)download.ProxyPasswordSecretRef ?? DBNull.Value);
        command.Parameters.AddWithValue("$media_kind", (object?)download.MediaKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$media_audio_url", (object?)download.MediaAudioUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$media_container", (object?)download.MediaContainer ?? DBNull.Value);
    }

    private static Download Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Url = reader.GetString(1),
        Referrer = reader.GetNullableString(2),
        Filename = reader.GetNullableString(3),
        Directory = reader.GetNullableString(4),
        TotalBytes = reader.GetNullableInt64(5),
        Status = reader.GetString(6),
        CategoryType = reader.GetNullableString(7),
        CategoryStatus = reader.GetNullableString(8),
        ETag = reader.GetNullableString(9),
        LastModified = reader.GetNullableString(10),
        CreatedAt = reader.GetDateTimeOffset(11),
        CompletedAt = reader.GetNullableDateTimeOffset(12),
        Error = reader.GetNullableString(13),
        MaxConnections = reader.GetNullableInt32(14),
        SpeedLimit = reader.GetNullableInt64(15),
        Priority = reader.GetInt32(16),
        CookieSecretRef = reader.GetNullableString(17),
        RetryCount = reader.GetInt32(18),
        ProxyKind = reader.GetNullableInt32(19),
        ProxyHost = reader.GetNullableString(20),
        ProxyPort = reader.GetNullableInt32(21),
        ProxyUsername = reader.GetNullableString(22),
        ProxyDomain = reader.GetNullableString(23),
        ProxyPasswordSecretRef = reader.GetNullableString(24),
        MediaKind = reader.GetNullableInt32(25),
        MediaAudioUrl = reader.GetNullableString(26),
        MediaContainer = reader.GetNullableInt32(27),
    };

    public async Task<IReadOnlyList<Download>> GetByStatusOrderedByPriorityAsync(
        string statusCode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(statusCode);

        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {Columns} FROM downloads WHERE status = $status " +
            "ORDER BY priority DESC, created_at ASC, id ASC;";
        command.Parameters.AddWithValue("$status", statusCode);

        var results = new List<Download>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<bool> SetPriorityAsync(long id, int priority, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE downloads SET priority = $priority WHERE id = $id;";
        command.Parameters.AddWithValue("$priority", priority);
        command.Parameters.AddWithValue("$id", id);

        int rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task SetPrioritiesAsync(
        IReadOnlyList<DownloadPriority> priorities, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(priorities);
        if (priorities.Count == 0)
        {
            return;
        }

        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        // One UPDATE with a CASE so the whole reorder is a single round-trip. Fully parameterized; the only
        // interpolated text is the parameter indices (plain integers), never any value.
        var cases = new System.Text.StringBuilder("UPDATE downloads SET priority = CASE id");
        var ids = new List<string>(priorities.Count);
        for (int i = 0; i < priorities.Count; i++)
        {
            cases.Append(" WHEN $id").Append(i).Append(" THEN $p").Append(i);
            command.Parameters.AddWithValue("$id" + i, priorities[i].Id);
            command.Parameters.AddWithValue("$p" + i, priorities[i].Priority);
            ids.Add("$id" + i);
        }

        cases.Append(" END WHERE id IN (").AppendJoin(", ", ids).Append(");");
        command.CommandText = cases.ToString();

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> MarkAllAsync(
        string fromStatus, string toStatus, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(fromStatus);
        ArgumentException.ThrowIfNullOrEmpty(toStatus);

        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE downloads SET status = $to WHERE status = $from;";
        command.Parameters.AddWithValue("$to", toStatus);
        command.Parameters.AddWithValue("$from", fromStatus);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
