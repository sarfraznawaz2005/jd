using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Data;
using JustDownload.Core.Data.Migrations;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Downloading;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Security;
using JustDownload.Core.Transport.Proxy;
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

        public Dictionary<string, string> Values => _values;

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
    public async Task Enqueue_ProxyOverridePassword_IsStoredInKeychain_NotInTheClear()
    {
        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = new Uri("https://example.com/file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "file.bin",
            Proxy = new ProxyConfiguration(
                ProxyKind.Http, "proxy.local", 8080,
                new JustDownload.Core.Transport.Auth.NetworkCredentials("user", "s3cret", "CORP")),
        });

        Download? saved = await Repository.GetAsync(id);
        saved!.ProxyKind.Should().Be((int)ProxyKind.Http);
        saved.ProxyHost.Should().Be("proxy.local");
        saved.ProxyUsername.Should().Be("user");
        saved.ProxyDomain.Should().Be("CORP");
        saved.ProxyPasswordSecretRef.Should().NotBeNullOrEmpty("the proxy password is referenced by a keychain ref");

        // The plaintext password lives only in the keychain (here, the in-memory store), never in a column (§5).
        _secrets.Values[saved.ProxyPasswordSecretRef!].Should().Be("s3cret");
        saved.ProxyDomain.Should().NotContain("s3cret");
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

    /// <summary>
    /// Captured cookies must not outlive the download they authenticate (§5). A completed download can never
    /// need them again — resume is impossible — so the keychain entry is deleted and the reference cleared.
    /// Before this, a browser session cookie stayed in the OS keychain for the life of the record
    /// (user-reported: 18 finished downloads still holding cookies in Settings > Authentication).
    /// </summary>
    [Fact]
    public async Task Complete_DeletesTheCookiesFromTheKeychain_AndClearsTheRef()
    {
        byte[] body = Bytes(80 * 1024);
        await using var server = new LoopbackHttpServer { Body = body, SupportRanges = true };

        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "done.bin",
            MaxConnections = 1,
            Cookies = "session=abc123",
        });

        string reference = (await Repository.GetAsync(id))!.CookieSecretRef!;
        _secrets.Values.Should().ContainKey(reference);

        await Manager.StartAsync(id);

        Download? saved = await Repository.GetAsync(id);
        saved!.Status.Should().Be(DownloadStatusCodes.Completed);
        saved.CookieSecretRef.Should().BeNull("a completed download no longer references any cookies");
        _secrets.Values.Should().NotContainKey(reference, "the keychain entry itself is gone, not just the ref");
    }

    /// <summary>
    /// The flip side: every non-completed terminal state is resumable, and resume re-sends the Cookie header
    /// from the keychain — so a pause must keep the credential. Purging on any terminal state (rather than
    /// only completion) would silently break resuming a cookie-gated link.
    /// </summary>
    [Fact]
    public async Task Pause_KeepsTheCookies_SoResumeStillAuthenticates()
    {
        const int fileSize = 512 * 1024;
        byte[] body = Bytes(fileSize);
        await using var server = new LoopbackHttpServer
        {
            Body = body,
            SupportRanges = true,
            SlowTailFrom = 128 * 1024,
            SlowTailDelay = TimeSpan.FromMilliseconds(600),
        };

        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "paused.bin",
            MaxConnections = 4,
            Cookies = "session=abc123",
        });

        string reference = (await Repository.GetAsync(id))!.CookieSecretRef!;

        using var pauseCts = new CancellationTokenSource();
        int cancelled = 0;
        Manager.ProgressChanged += (_, e) =>
        {
            if (e.DownloadId == id && e.Progress.DownloadedBytes >= 64 * 1024 &&
                Interlocked.Exchange(ref cancelled, 1) == 0)
            {
                pauseCts.Cancel();
            }
        };

        Func<Task> paused = async () => await Manager.StartAsync(id, pauseCts.Token);
        await paused.Should().ThrowAsync<OperationCanceledException>();

        Download? afterPause = await Repository.GetAsync(id);
        afterPause!.Status.Should().Be(DownloadStatusCodes.Paused);
        afterPause.CookieSecretRef.Should().Be(reference, "a paused download can still be resumed");
        _secrets.Values.Should().ContainKey(reference);

        server.ClearReceivedHeaderLines();
        await Manager.StartAsync(id);

        server.ReceivedHeaderLines.Should().Contain(
            l => l.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase) && l.Contains("session=abc123"),
            "the resume re-sends the retained cookies");
        (await File.ReadAllBytesAsync(Path.Combine(_tempDir, "paused.bin"))).Should().Equal(body);

        // …and now that it has completed, they go.
        (await Repository.GetAsync(id))!.CookieSecretRef.Should().BeNull();
        _secrets.Values.Should().NotContainKey(reference);
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
