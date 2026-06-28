using System.Globalization;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Data;
using JustDownload.Core.Data.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Data;

/// <summary>
/// Verifies the versioned migration runner (TASK-019): a fresh database gets every PRD §4.4 table,
/// the schema version is tracked via <c>PRAGMA user_version</c>, and re-running is idempotent. Each
/// test uses an isolated temp database directory and clears connection pools before deleting it.
/// </summary>
public sealed class MigrationRunnerTests : IDisposable
{
    private static readonly string[] ExpectedTables =
    {
        "downloads",
        "segments",
        "auth",
        "proxies",
        "extractor_jobs",
        "settings",
        "site_blacklist",
    };

    private readonly string _tempDir;
    private readonly IDatabasePathProvider _pathProvider;

    public MigrationRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-mig-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _pathProvider = Substitute.For<IDatabasePathProvider>();
        _pathProvider.DatabaseDirectory.Returns(_tempDir);
        _pathProvider.DatabasePath.Returns(Path.Combine(_tempDir, "test.db"));
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_pathProvider);
        services.AddJustDownloadData();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Migrate_OnFreshDatabase_CreatesAllPrdTables()
    {
        using ServiceProvider provider = BuildProvider();
        var runner = provider.GetRequiredService<IMigrationRunner>();
        var factory = provider.GetRequiredService<IDbConnectionFactory>();

        int version = runner.Migrate();

        version.Should().Be(2, "the migration head is version 2 after TASK-072");
        GetTableNames(factory).Should().Contain(ExpectedTables);
    }

    [Fact]
    public void Migrate_TracksVersionViaUserVersion()
    {
        using ServiceProvider provider = BuildProvider();
        var runner = provider.GetRequiredService<IMigrationRunner>();
        var factory = provider.GetRequiredService<IDbConnectionFactory>();

        runner.Migrate();

        using SqliteConnection connection = factory.CreateOpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture).Should().Be(2);
    }

    [Fact]
    public void Migrate_RunTwice_IsIdempotentAndVersionStable()
    {
        using ServiceProvider provider = BuildProvider();
        var runner = provider.GetRequiredService<IMigrationRunner>();
        var factory = provider.GetRequiredService<IDbConnectionFactory>();

        int first = runner.Migrate();

        // A second run must not throw (no "table already exists") and must leave the version stable.
        Action secondRun = () => runner.Migrate();
        secondRun.Should().NotThrow();

        int second = runner.Migrate();

        first.Should().Be(2);
        second.Should().Be(2);
        GetTableNames(factory).Should().Contain(ExpectedTables);
    }

    [Fact]
    public async Task MigrateAsync_OnFreshDatabase_CreatesAllPrdTablesAndIsIdempotent()
    {
        using ServiceProvider provider = BuildProvider();
        var runner = provider.GetRequiredService<IMigrationRunner>();
        var factory = provider.GetRequiredService<IDbConnectionFactory>();

        int first = await runner.MigrateAsync();
        int second = await runner.MigrateAsync();

        first.Should().Be(2);
        second.Should().Be(2);
        GetTableNames(factory).Should().Contain(ExpectedTables);
    }

    [Fact]
    public void Migrate_EnforcesForeignKeyCascade_OnSegments()
    {
        // Proves the schema wired the FK + cascade (segments -> downloads) the runner created.
        using ServiceProvider provider = BuildProvider();
        provider.GetRequiredService<IMigrationRunner>().Migrate();
        var factory = provider.GetRequiredService<IDbConnectionFactory>();

        using SqliteConnection connection = factory.CreateOpenConnection();

        ExecNonQuery(connection,
            "INSERT INTO downloads (url, status, created_at) VALUES ('http://x/f', 'queued', '2026-06-26T00:00:00Z');");
        long downloadId = LastRowId(connection);

        ExecNonQuery(connection, string.Format(
            CultureInfo.InvariantCulture,
            "INSERT INTO segments (download_id, \"index\", \"start\", \"end\", state) VALUES ({0}, 0, 0, 1023, 'pending');",
            downloadId));

        ExecNonQuery(connection, string.Format(
            CultureInfo.InvariantCulture, "DELETE FROM downloads WHERE id = {0};", downloadId));

        using SqliteCommand count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM segments;";
        Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture)
            .Should().Be(0, "ON DELETE CASCADE should remove the download's segments");
    }

    [Fact]
    public void Migrate_StoresAuthSecretOnlyAsReference()
    {
        // The auth/proxies tables persist a secret_ref pointer, never a plaintext secret (§5).
        using ServiceProvider provider = BuildProvider();
        provider.GetRequiredService<IMigrationRunner>().Migrate();
        var factory = provider.GetRequiredService<IDbConnectionFactory>();

        IReadOnlyCollection<string> authColumns = GetColumnNames(factory, "auth");
        authColumns.Should().Contain("secret_ref");
        authColumns.Should().NotContain(c => c == "password" || c == "secret" || c == "credential");

        GetColumnNames(factory, "proxies").Should().Contain("secret_ref");
    }

    private static List<string> GetTableNames(IDbConnectionFactory factory)
    {
        using SqliteConnection connection = factory.CreateOpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";

        var names = new List<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static List<string> GetColumnNames(IDbConnectionFactory factory, string table)
    {
        using SqliteConnection connection = factory.CreateOpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = string.Format(CultureInfo.InvariantCulture, "PRAGMA table_info('{0}');", table);

        var columns = new List<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // PRAGMA table_info columns: cid, name, type, notnull, dflt_value, pk.
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static void ExecNonQuery(SqliteConnection connection, string sql)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long LastRowId(SqliteConnection connection)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; the OS temp dir is reclaimed regardless.
        }
    }
}
