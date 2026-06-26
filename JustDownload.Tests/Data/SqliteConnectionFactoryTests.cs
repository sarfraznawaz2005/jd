using System.Collections.Concurrent;
using System.Globalization;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Data;

/// <summary>
/// Verifies the SQLite data layer (TASK-018): WAL is enabled, the path resolves under the per-OS
/// application-data directory, and concurrent readers/writers are safe. Everything is exercised
/// through the public DI surface (matching the Core convention of testing internals via the
/// composition root). Each test uses an isolated temp database directory and clears connection
/// pools before deleting it.
/// </summary>
public sealed class SqliteConnectionFactoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IDatabasePathProvider _pathProvider;

    public SqliteConnectionFactoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _pathProvider = Substitute.For<IDatabasePathProvider>();
        _pathProvider.DatabaseDirectory.Returns(_tempDir);
        _pathProvider.DatabasePath.Returns(Path.Combine(_tempDir, "test.db"));
    }

    /// <summary>
    /// Builds a provider whose <see cref="IDbConnectionFactory"/> points at the temp database,
    /// optionally overriding <see cref="DatabaseOptions"/>. Pre-registration wins over the
    /// <c>TryAdd</c> defaults in <see cref="ServiceCollectionExtensions.AddJustDownloadData"/>.
    /// </summary>
    private ServiceProvider BuildProvider(DatabaseOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_pathProvider);
        if (options is not null)
        {
            services.AddSingleton(options);
        }

        services.AddJustDownloadData();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void CreateOpenConnection_EnablesWalJournalMode()
    {
        using ServiceProvider provider = BuildProvider();
        var factory = provider.GetRequiredService<IDbConnectionFactory>();

        using SqliteConnection connection = factory.CreateOpenConnection();

        connection.State.Should().Be(System.Data.ConnectionState.Open);

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        string? journalMode = (string?)cmd.ExecuteScalar();

        journalMode.Should().BeEquivalentTo("wal");
    }

    [Fact]
    public void CreateOpenConnection_SetsBusyTimeoutAndForeignKeys()
    {
        using ServiceProvider provider = BuildProvider(
            new DatabaseOptions { BusyTimeout = TimeSpan.FromSeconds(3) });
        var factory = provider.GetRequiredService<IDbConnectionFactory>();

        using SqliteConnection connection = factory.CreateOpenConnection();

        using SqliteCommand busyCmd = connection.CreateCommand();
        busyCmd.CommandText = "PRAGMA busy_timeout;";
        Convert.ToInt64(busyCmd.ExecuteScalar(), CultureInfo.InvariantCulture).Should().Be(3000);

        using SqliteCommand fkCmd = connection.CreateCommand();
        fkCmd.CommandText = "PRAGMA foreign_keys;";
        Convert.ToInt64(fkCmd.ExecuteScalar(), CultureInfo.InvariantCulture).Should().Be(1);
    }

    [Fact]
    public async Task CreateOpenConnectionAsync_EnablesWalJournalMode()
    {
        using ServiceProvider provider = BuildProvider();
        var factory = provider.GetRequiredService<IDbConnectionFactory>();

        await using SqliteConnection connection = await factory.CreateOpenConnectionAsync();

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        string? journalMode = await cmd.ExecuteScalarAsync() as string;

        journalMode.Should().BeEquivalentTo("wal");
    }

    [Fact]
    public void DatabasePathProvider_ResolvesUnderPerOsAppDataDir()
    {
        // The real path provider (no substitution) uses the per-OS application-data directory.
        using ServiceProvider provider = new ServiceCollection()
            .AddJustDownloadCore()
            .BuildServiceProvider();

        var pathProvider = provider.GetRequiredService<IDatabasePathProvider>();

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string expectedDir = Path.Combine(appData, "JustDownload");

        pathProvider.DatabaseDirectory.Should().Be(expectedDir);
        pathProvider.DatabasePath.Should().Be(Path.Combine(expectedDir, "justdownload.db"));
        // The resolved path lives beneath the OS application-data root, not a hard-coded folder.
        pathProvider.DatabasePath.Should().StartWith(appData);
    }

    [Fact]
    public async Task ConcurrentReadsAndWrites_AllSucceed()
    {
        using ServiceProvider provider = BuildProvider();
        var factory = provider.GetRequiredService<IDbConnectionFactory>();

        // One-time schema setup.
        using (SqliteConnection setup = factory.CreateOpenConnection())
        {
            using SqliteCommand create = setup.CreateCommand();
            create.CommandText = "CREATE TABLE items (id INTEGER PRIMARY KEY, value TEXT NOT NULL);";
            create.ExecuteNonQuery();
        }

        const int writerCount = 8;
        const int rowsPerWriter = 50;
        const int readerCount = 8;

        var exceptions = new ConcurrentQueue<Exception>();

        IEnumerable<Task> writers = Enumerable.Range(0, writerCount).Select(writerId => Task.Run(async () =>
        {
            try
            {
                for (int row = 0; row < rowsPerWriter; row++)
                {
                    await using SqliteConnection conn = await factory.CreateOpenConnectionAsync();
                    await using SqliteCommand insert = conn.CreateCommand();
                    insert.CommandText = "INSERT INTO items (value) VALUES ($v);";
                    insert.Parameters.AddWithValue("$v", $"w{writerId}-r{row}");
                    await insert.ExecuteNonQueryAsync();
                }
            }
            catch (Exception ex)
            {
                exceptions.Enqueue(ex);
            }
        }));

        IEnumerable<Task> readers = Enumerable.Range(0, readerCount).Select(readerId => Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < rowsPerWriter; i++)
                {
                    await using SqliteConnection conn = await factory.CreateOpenConnectionAsync();
                    await using SqliteCommand count = conn.CreateCommand();
                    count.CommandText = "SELECT COUNT(*) FROM items;";
                    _ = await count.ExecuteScalarAsync();
                }
            }
            catch (Exception ex)
            {
                exceptions.Enqueue(ex);
            }
        }));

        await Task.WhenAll(writers.Concat(readers));

        exceptions.Should().BeEmpty(
            "WAL + busy_timeout must absorb concurrent writers and readers without SQLITE_BUSY");

        using SqliteConnection verify = factory.CreateOpenConnection();
        using SqliteCommand final = verify.CreateCommand();
        final.CommandText = "SELECT COUNT(*) FROM items;";
        Convert.ToInt64(final.ExecuteScalar(), CultureInfo.InvariantCulture).Should().Be(writerCount * rowsPerWriter);
    }

    public void Dispose()
    {
        // Pooled connections keep the file handle open; release them before deleting the temp dir
        // so the .db / -wal / -shm files (and the directory) can be removed cleanly.
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
