using JustDownload.Core.Data.Models;
using Microsoft.Data.Sqlite;

namespace JustDownload.Core.Data.Repositories;

/// <summary>
/// Default <see cref="IBlacklistRepository"/> over <c>Microsoft.Data.Sqlite</c>. Insert uses
/// <c>ON CONFLICT DO NOTHING</c> so adding an existing (domain, scope) pair is idempotent rather
/// than a primary-key violation. Fully parameterized (CLAUDE.md §6).
/// </summary>
internal sealed class BlacklistRepository : IBlacklistRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public BlacklistRepository(IDbConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task AddAsync(BlacklistEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO site_blacklist (domain, scope) VALUES ($domain, $scope)
            ON CONFLICT (domain, scope) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$domain", entry.Domain);
        command.Parameters.AddWithValue("$scope", entry.Scope);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ExistsAsync(
        string domain, string scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(domain);
        ArgumentException.ThrowIfNullOrEmpty(scope);

        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM site_blacklist WHERE domain = $domain AND scope = $scope LIMIT 1;";
        command.Parameters.AddWithValue("$domain", domain);
        command.Parameters.AddWithValue("$scope", scope);

        object? hit = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return hit is not null;
    }

    public async Task<IReadOnlyList<BlacklistEntry>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT domain, scope FROM site_blacklist ORDER BY domain ASC, scope ASC;";

        var results = new List<BlacklistEntry>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new BlacklistEntry { Domain = reader.GetString(0), Scope = reader.GetString(1) });
        }

        return results;
    }

    public async Task<bool> DeleteAsync(
        string domain, string scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(domain);
        ArgumentException.ThrowIfNullOrEmpty(scope);

        await using SqliteConnection connection =
            await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM site_blacklist WHERE domain = $domain AND scope = $scope;";
        command.Parameters.AddWithValue("$domain", domain);
        command.Parameters.AddWithValue("$scope", scope);

        int rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows > 0;
    }
}
