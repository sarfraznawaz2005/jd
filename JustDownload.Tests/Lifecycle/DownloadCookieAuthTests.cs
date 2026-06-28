using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Data;
using JustDownload.Core.Data.Migrations;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Downloading;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Security;
using JustDownload.Tests.Transport;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>
/// Authenticated browser hand-off (TASK-091): cookies captured by the extension are kept only in the OS
/// keychain (never SQLite — only the opaque reference is persisted), and are resent as a <c>Cookie</c> header
/// — alongside <c>Referer</c> — on download/resume so a cookie-gated/signed link succeeds.
/// </summary>
public sealed class DownloadCookieAuthTests : IDisposable
{
    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private int _counter;

        public IReadOnlyDictionary<string, string> Values => _values;

        public Task<string> StoreAsync(string secret, CancellationToken cancellationToken = default)
        {
            string reference = "ref-" + System.Threading.Interlocked.Increment(ref _counter);
            _values[reference] = secret;
            return Task.FromResult(reference);
        }

        public Task<string?> RetrieveAsync(string secretRef, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.TryGetValue(secretRef, out string? v) ? v : null);

        public Task<bool> DeleteAsync(string secretRef, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.Remove(secretRef));
    }

    private readonly string _tempDir;
    private readonly ServiceProvider _provider;
    private readonly InMemorySecretStore _secrets = new();

    public DownloadCookieAuthTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-cookie-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var pathProvider = Substitute.For<IDatabasePathProvider>();
        pathProvider.DatabaseDirectory.Returns(_tempDir);
        pathProvider.DatabasePath.Returns(Path.Combine(_tempDir, "test.db"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(pathProvider);
        // Register the in-memory store first so AddJustDownloadSecrets' TryAdd keeps it (no real keychain in tests).
        services.AddSingleton<ISecretStore>(_secrets);
        services.AddSingleton(new SegmentationOptions
        {
            DefaultConnections = 1,
            MinSegmentSize = 16 * 1024,
            MinStealSize = 16 * 1024,
        });
        services.AddJustDownloadData();
        services.AddJustDownloadTransport();
        services.AddJustDownloadDownloading();
        services.AddJustDownloadLifecycle();
        _provider = services.BuildServiceProvider();
        _provider.GetRequiredService<IMigrationRunner>().Migrate();
    }

    private IDownloadManager Manager => _provider.GetRequiredService<IDownloadManager>();

    private IDownloadRepository Repository => _provider.GetRequiredService<IDownloadRepository>();

    private static byte[] Bytes(int count)
    {
        var data = new byte[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = (byte)((i * 17 + 5) % 256);
        }

        return data;
    }

    [Fact]
    public async Task Enqueue_PersistsOnlyKeychainRef_NotPlaintextCookies()
    {
        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = new Uri("https://example.com/file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "file.bin",
            Referrer = "https://example.com/watch",
            Cookies = "session=abc123; theme=dark",
        });

        Download? saved = await Repository.GetAsync(id);
        saved!.CookieSecretRef.Should().NotBeNullOrEmpty("cookies are referenced by an opaque keychain ref");
        saved.Referrer.Should().Be("https://example.com/watch");

        // The plaintext cookies live only in the keychain (here, the in-memory store), never in the record.
        _secrets.Values[saved.CookieSecretRef!].Should().Be("session=abc123; theme=dark");
    }

    [Fact]
    public async Task Start_SendsCookieAndRefererHeaders_FromKeychain()
    {
        byte[] body = Bytes(80 * 1024);
        await using var server = new LoopbackHttpServer { Body = body, SupportRanges = true };

        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "out.bin",
            MaxConnections = 1,
            Referrer = "https://example.com/watch",
            Cookies = "session=abc123",
        });

        DownloadResult result = await Manager.StartAsync(id);

        result.TotalBytes.Should().Be(body.Length);
        (await File.ReadAllBytesAsync(Path.Combine(_tempDir, "out.bin"))).Should().Equal(body);

        server.ReceivedHeaderLines.Should().Contain(
            l => l.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase) && l.Contains("session=abc123"),
            "the captured cookies are resent as a Cookie header");
        server.ReceivedHeaderLines.Should().Contain(
            l => l.StartsWith("Referer:", StringComparison.OrdinalIgnoreCase) && l.Contains("example.com/watch"),
            "the referrer is resent as a Referer header");
    }

    [Fact]
    public async Task Repository_RoundTrips_CookieSecretRef()
    {
        long id = await Repository.AddAsync(new Download
        {
            Url = "https://example.com/x",
            Status = DownloadStatusCodes.Queued,
            CookieSecretRef = "ref-xyz",
        });

        (await Repository.GetAsync(id))!.CookieSecretRef.Should().Be("ref-xyz");
    }

    public void Dispose()
    {
        _provider.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
