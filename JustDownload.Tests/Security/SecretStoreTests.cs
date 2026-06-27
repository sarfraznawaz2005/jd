using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Data;
using JustDownload.Core.Data.Migrations;
using JustDownload.Core.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Security;

/// <summary>
/// Tests for the OS keychain secret storage (TASK-022). The DPAPI round-trip is runtime-verified on
/// Windows; the DB-contract test proves that only an opaque <c>secret_ref</c> — never the plaintext
/// secret — is persisted in SQLite (CLAUDE.md §5, PRD §4.4/§4.6). The macOS/Linux stores are
/// implemented for parity and exercised on their own OS, not here.
/// </summary>
public sealed class SecretStoreTests : IDisposable
{
    private const string SampleSecret = "hunter2-Sup3r!Secret-Pa$$w0rd-7f3a9c";

    private readonly string _tempDir;

    public SecretStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-secret-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // A lingering handle on a CI runner shouldn't fail the suite; the temp dir is reaped by the OS.
        }
    }

    private ISecretStorePathProvider PathProvider()
    {
        var provider = Substitute.For<ISecretStorePathProvider>();
        provider.SecretsDirectory.Returns(Path.Combine(_tempDir, "secrets"));
        return provider;
    }

    [Fact]
    public async Task WindowsDpapi_Store_Then_Retrieve_RoundTrips()
    {
        // DPAPI is Windows-only; the early return keeps this a no-op on other OSes and satisfies the
        // platform-compatibility analyzer for the WindowsDpapiSecretStore reference below.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new WindowsDpapiSecretStore(PathProvider());

        string secretRef = await store.StoreAsync(SampleSecret);
        string? retrieved = await store.RetrieveAsync(secretRef);

        secretRef.Should().NotBeNullOrEmpty().And.NotBe(SampleSecret);
        retrieved.Should().Be(SampleSecret);
    }

    [Fact]
    public async Task WindowsDpapi_Retrieve_ReturnsNull_AfterDelete()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new WindowsDpapiSecretStore(PathProvider());
        string secretRef = await store.StoreAsync(SampleSecret);

        bool deleted = await store.DeleteAsync(secretRef);
        string? retrieved = await store.RetrieveAsync(secretRef);

        deleted.Should().BeTrue();
        retrieved.Should().BeNull();
        (await store.DeleteAsync(secretRef)).Should().BeFalse("a second delete finds nothing");
    }

    [Fact]
    public async Task WindowsDpapi_VaultFile_NeverContainsPlaintext()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new WindowsDpapiSecretStore(PathProvider());
        await store.StoreAsync(SampleSecret);

        string secretsDir = Path.Combine(_tempDir, "secrets");
        byte[] secretBytes = Encoding.UTF8.GetBytes(SampleSecret);
        foreach (string file in Directory.EnumerateFiles(secretsDir, "*", SearchOption.AllDirectories))
        {
            byte[] contents = await File.ReadAllBytesAsync(file);
            IndexOf(contents, secretBytes).Should().Be(-1, "the DPAPI blob must not contain plaintext");
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task WindowsDpapi_RejectsUnsafeReference_PathTraversal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new WindowsDpapiSecretStore(PathProvider());

        Func<Task> act = () => store.RetrieveAsync("../escape");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AuthRow_PersistsOnlySecretRef_NotTheSecretValue()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Stand up a real, isolated SQLite database migrated to the PRD §4.4 schema.
        var pathProvider = Substitute.For<IDatabasePathProvider>();
        pathProvider.DatabaseDirectory.Returns(_tempDir);
        pathProvider.DatabasePath.Returns(Path.Combine(_tempDir, "test.db"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(pathProvider);
        services.AddSingleton(PathProvider());
        services.AddJustDownloadData();
        services.AddJustDownloadSecrets();
        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMigrationRunner>().Migrate();

        var store = provider.GetRequiredService<ISecretStore>();
        var connectionFactory = provider.GetRequiredService<IDbConnectionFactory>();

        // The secret goes to the OS vault; only the returned reference is bound for the DB.
        string secretRef = await store.StoreAsync(SampleSecret);

        await using (SqliteConnection connection = await connectionFactory.CreateOpenConnectionAsync())
        {
            await using SqliteCommand insertDownload = connection.CreateCommand();
            insertDownload.CommandText =
                "INSERT INTO downloads (url, status, created_at) VALUES ('https://example.com/f', 'queued', '2026-06-27T00:00:00+00:00');";
            await insertDownload.ExecuteNonQueryAsync();

            await using SqliteCommand insertAuth = connection.CreateCommand();
            insertAuth.CommandText =
                """
                INSERT INTO auth (download_id, scheme, realm, username, secret_ref)
                VALUES (last_insert_rowid(), 'Basic', 'example', 'alice', $ref);
                """;
            insertAuth.Parameters.AddWithValue("$ref", secretRef);
            await insertAuth.ExecuteNonQueryAsync();

            // Flush the WAL into the main db file so the on-disk scan below sees everything.
            await using SqliteCommand checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync();
        }

        // The auth row stores exactly the reference in secret_ref — and the plaintext appears in no column.
        await using (SqliteConnection connection = await connectionFactory.CreateOpenConnectionAsync())
        {
            await using SqliteCommand read = connection.CreateCommand();
            read.CommandText = "SELECT scheme, realm, username, secret_ref FROM auth;";
            await using SqliteDataReader reader = await read.ExecuteReaderAsync();

            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(3).Should().Be(secretRef, "only the reference is persisted");
            for (int i = 0; i < reader.FieldCount; i++)
            {
                reader.GetString(i).Should().NotBe(SampleSecret);
            }
        }

        // And no SQLite file (db / -wal / -shm) contains the plaintext bytes anywhere.
        SqliteConnection.ClearAllPools();
        byte[] secretBytes = Encoding.UTF8.GetBytes(SampleSecret);
        foreach (string file in Directory.EnumerateFiles(_tempDir, "test.db*", SearchOption.TopDirectoryOnly))
        {
            byte[] contents = await File.ReadAllBytesAsync(file);
            IndexOf(contents, secretBytes).Should().Be(-1, $"{Path.GetFileName(file)} must hold no plaintext secret");
        }

        // The secret is still recoverable from the vault via the persisted reference.
        (await store.RetrieveAsync(secretRef)).Should().Be(SampleSecret);
    }

    [Fact]
    public void AddJustDownloadCore_RegistersPlatformSecretStore()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddJustDownloadCore()
            .BuildServiceProvider();

        var store = provider.GetRequiredService<ISecretStore>();

        if (OperatingSystem.IsWindows())
        {
            store.Should().BeOfType<WindowsDpapiSecretStore>();
        }
        else
        {
            store.Should().NotBeNull();
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return -1;
        }

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j])
            {
                j++;
            }

            if (j == needle.Length)
            {
                return i;
            }
        }

        return -1;
    }
}
