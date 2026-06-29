using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Data;
using JustDownload.Core.Data.Migrations;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Downloading;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Transport.Proxy;
using JustDownload.Tests.Fakes;
using JustDownload.Tests.Transport;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>
/// Integration tests for the lifecycle orchestrator (TASK-031) against a real temp SQLite database and a
/// loopback HTTP server: enqueue persists a queued record (AC2), a successful start drives queued → active →
/// complete with persisted state, status/progress events, and a 100%-progress snapshot (AC1/AC2), a network
/// failure transitions to error with the message recorded, and an already-completed download cannot restart
/// (AC0 — the state machine is enforced end to end).
/// </summary>
public sealed class DownloadManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ServiceProvider _provider;

    public DownloadManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-mgr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var pathProvider = Substitute.For<IDatabasePathProvider>();
        pathProvider.DatabaseDirectory.Returns(_tempDir);
        pathProvider.DatabasePath.Returns(Path.Combine(_tempDir, "test.db"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(pathProvider);
        services.AddSingleton(new SegmentationOptions
        {
            DefaultConnections = 4,
            MinSegmentSize = 16 * 1024,
            MinStealSize = 16 * 1024,
        });
        services.AddJustDownloadData();
        services.AddJustDownloadTransport();
        services.AddJustDownloadDownloading();
        services.AddJustDownloadLifecycle();
        // No auto-retry here so the failure-path tests fail fast and deterministically; retry behaviour is
        // covered by DownloadRetryTests (TASK-131).
        services.AddSingleton<IRetryBackoff>(new FakeRetryBackoff(0));
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
            data[i] = (byte)((i * 31 + 7) % 256);
        }

        return data;
    }

    [Fact]
    public async Task EnqueueAsync_PersistsQueuedRecord_AndRaisesStatusEvent()
    {
        var events = new List<DownloadStatusChangedEventArgs>();
        Manager.StatusChanged += (_, e) => events.Add(e);

        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = new Uri("https://example.com/file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "file.bin",
            MaxConnections = 4,
        });

        id.Should().BePositive();
        Download? saved = await Repository.GetAsync(id);
        saved.Should().NotBeNull();
        saved!.Status.Should().Be(DownloadStatusCodes.Queued);

        events.Should().ContainSingle();
        events[0].DownloadId.Should().Be(id);
        events[0].Previous.Should().BeNull();
        events[0].Current.Should().Be(DownloadStatus.Queued);
    }

    [Fact]
    public async Task StartAsync_RunsToCompletion_PersistsState_AndRaisesEvents()
    {
        byte[] body = Bytes(200 * 1024);
        await using var server = new LoopbackHttpServer { Body = body, SupportRanges = true };

        var statusEvents = new List<DownloadStatusChangedEventArgs>();
        var progressEvents = new List<DownloadProgress>();
        Manager.StatusChanged += (_, e) => statusEvents.Add(e);
        Manager.ProgressChanged += (_, e) => progressEvents.Add(e.Progress);

        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "out.bin",
            MaxConnections = 4,
        });

        DownloadResult result = await Manager.StartAsync(id);

        result.TotalBytes.Should().Be(body.Length);
        (await File.ReadAllBytesAsync(Path.Combine(_tempDir, "out.bin"))).Should().Equal(body);

        // Persisted terminal state.
        Download? saved = await Repository.GetAsync(id);
        saved!.Status.Should().Be(DownloadStatusCodes.Completed);
        saved.CompletedAt.Should().NotBeNull();
        saved.TotalBytes.Should().Be(body.Length);
        saved.CategoryStatus.Should().Be("Complete");
        saved.Error.Should().BeNull();

        // Event sequence: queued → active → complete.
        statusEvents.Select(e => e.Current).Should()
            .ContainInOrder(DownloadStatus.Queued, DownloadStatus.Active, DownloadStatus.Completed);

        // Progress was observed and the final snapshot is 100% complete.
        progressEvents.Should().NotBeEmpty();
        DownloadProgress final = progressEvents[^1];
        final.Status.Should().Be(DownloadStatus.Completed);
        final.DownloadedBytes.Should().Be(body.Length);
        final.Fraction.Should().BeApproximately(1.0, 1e-9);

        Manager.GetProgress(id).Should().NotBeNull();
        Manager.GetProgress(id)!.DownloadedBytes.Should().Be(body.Length);
    }

    [Fact]
    public async Task PerDownloadProxyOverride_PersistsAndRoutesThatDownloadThroughTheProxy()
    {
        // The global proxy is None (direct) in this harness, so traffic hitting the loopback SOCKS proxy can
        // only be the per-download override taking effect (TASK-153).
        byte[] body = Bytes(64 * 1024);
        await using var origin = new LoopbackHttpServer { Body = body, SupportRanges = true };
        await using var socks = new LoopbackSocksProxy();

        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = origin.Url("f.bin"),
            DestinationDirectory = _tempDir,
            FileName = "via-override.bin",
            Proxy = new ProxyConfiguration(ProxyKind.Socks5, "127.0.0.1", socks.Port),
        });

        Download? persisted = await Repository.GetAsync(id);
        persisted!.ProxyKind.Should().Be((int)ProxyKind.Socks5);
        persisted.ProxyHost.Should().Be("127.0.0.1");
        persisted.ProxyPort.Should().Be(socks.Port);

        DownloadResult result = await Manager.StartAsync(id);

        result.TotalBytes.Should().Be(body.Length);
        (await File.ReadAllBytesAsync(Path.Combine(_tempDir, "via-override.bin"))).Should().Equal(body);
        socks.ConnectedTargets.Should().NotBeEmpty(
            "the download was routed through the per-download proxy override, not direct");
    }

    [Fact]
    public async Task MediaDownload_Hls_DownloadsAndConcatenates_AsTrackedDownload()
    {
        // A chosen HLS variant runs the segments->concat media path (TASK-154) and lands as a tracked,
        // Completed download whose output is the segments joined in playlist order.
        var segments = new[] { Bytes(4096), Bytes(8192), Bytes(2048) };
        await using var hls = new LoopbackHlsServer(segments, encrypted: false);

        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = hls.MediaUrl,
            DestinationDirectory = _tempDir,
            FileName = "video.ts",
            MediaKind = JustDownload.Core.Media.Extraction.MediaKind.Hls,
        });

        DownloadResult result = await Manager.StartAsync(id);

        string output = Path.Combine(_tempDir, "video.ts");
        (await File.ReadAllBytesAsync(output)).Should().Equal(hls.ReferenceBytes, "segments concatenated in playlist order");
        result.TotalBytes.Should().Be(hls.ReferenceBytes.Length);

        Download? saved = await Repository.GetAsync(id);
        saved!.Status.Should().Be(DownloadStatusCodes.Completed);
        saved.MediaKind.Should().Be((int)JustDownload.Core.Media.Extraction.MediaKind.Hls);
        Directory.Exists(output + ".jdmedia").Should().BeFalse("the scratch segment directory is cleaned up");

        Manager.GetProgress(id)!.Fraction.Should().Be(1.0);
    }

    [Fact]
    public async Task PerDownloadProxyOverride_IsUsedForThePreflightProbe_NotJustTheTransfer()
    {
        // The global proxy is dead; only the per-download override can reach the origin. If the validator
        // probe ignored the override (TASK-157) it would fail on the dead global and the download couldn't
        // capture validators / complete. Success here proves the probe routes through the override.
        byte[] body = Bytes(64 * 1024);
        await using var origin = new LoopbackHttpServer { Body = body, SupportRanges = true };
        await using var socks = new LoopbackSocksProxy();

        var proxyService = _provider.GetRequiredService<IProxyService>();
        proxyService.SetGlobalProxy(new ProxyConfiguration(ProxyKind.Socks5, "127.0.0.1", ClosedLoopbackPort()));

        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = origin.Url("f.bin"),
            DestinationDirectory = _tempDir,
            FileName = "via-override-probe.bin",
            Proxy = new ProxyConfiguration(ProxyKind.Socks5, "127.0.0.1", socks.Port),
        });

        DownloadResult result = await Manager.StartAsync(id);

        (await File.ReadAllBytesAsync(Path.Combine(_tempDir, "via-override-probe.bin"))).Should().Equal(body);
        Download? saved = await Repository.GetAsync(id);
        saved!.TotalBytes.Should().Be(body.Length, "the probe captured validators through the override proxy");
    }

    private static int ClosedLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task StartAsync_OnNetworkFailure_TransitionsToError_AndRecordsMessage()
    {
        // The .invalid TLD never resolves (RFC 2606), so the probe fails deterministically with no network.
        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = new Uri("http://nonexistent.invalid/file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "bad.bin",
        });

        Func<Task> act = async () => await Manager.StartAsync(id);
        await act.Should().ThrowAsync<Exception>();

        Download? saved = await Repository.GetAsync(id);
        saved!.Status.Should().Be(DownloadStatusCodes.Failed);
        saved.Error.Should().NotBeNullOrEmpty();
        saved.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_OnCompletedDownload_ThrowsAndDoesNotRestart()
    {
        byte[] body = Bytes(64 * 1024);
        await using var server = new LoopbackHttpServer { Body = body, SupportRanges = true };

        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "once.bin",
        });
        await Manager.StartAsync(id);

        Func<Task> act = async () => await Manager.StartAsync(id);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Illegal download transition*");
    }

    [Fact]
    public async Task StartAsync_UnknownId_Throws()
    {
        Func<Task> act = async () => await Manager.StartAsync(99999);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    public void Dispose()
    {
        _provider.Dispose();
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
