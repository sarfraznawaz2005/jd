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
    public async Task MediaDownload_SeparateStreams_DownloadsBothAndMuxes_ToFfprobeValidOutput()
    {
        // AC[0]: a separate video+audio variant downloads end-to-end and muxes into one ffprobe-valid file.
        var ffmpeg = _provider.GetRequiredService<JustDownload.Core.Media.IFfmpegLocator>();
        if (await ffmpeg.LocateAsync() is null)
        {
            return; // ffmpeg/ffprobe not installed on this host.
        }

        string videoFixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.ts");
        if (!File.Exists(videoFixture))
        {
            return;
        }

        // Generate a short AAC audio track to pair with the H.264 .ts video fixture.
        string audioFile = Path.Combine(_tempDir, "tone.m4a");
        var gen = await _provider.GetRequiredService<JustDownload.Core.Media.IFfmpegRunner>().RunAsync(
            ["-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:a", "aac", audioFile]);
        if (!gen.Succeeded)
        {
            return;
        }

        await using var videoServer = new LoopbackHttpServer { Body = await File.ReadAllBytesAsync(videoFixture), SupportRanges = true };
        await using var audioServer = new LoopbackHttpServer { Body = await File.ReadAllBytesAsync(audioFile), SupportRanges = true };

        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = videoServer.Url("v.ts"),
            MediaAudioUrl = audioServer.Url("a.m4a"),
            MediaKind = JustDownload.Core.Media.Extraction.MediaKind.SeparateStreams,
            MediaContainer = JustDownload.Core.Settings.MediaContainer.Mkv,
            DestinationDirectory = _tempDir,
            FileName = "muxed.mkv",
        });

        await Manager.StartAsync(id);

        string output = Path.Combine(_tempDir, "muxed.mkv");
        File.Exists(output).Should().BeTrue();
        (await Repository.GetAsync(id))!.Status.Should().Be(DownloadStatusCodes.Completed);

        string streams = await ProbeCodecsAsync(output);
        streams.Should().Contain("video", "the muxed file has the video stream");
        streams.Should().Contain("audio", "the muxed file has the audio stream");
    }

    private static async Task<string> ProbeCodecsAsync(string path)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("stream=codec_type");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("csv=p=0");
        psi.ArgumentList.Add(path);

        using var process = System.Diagnostics.Process.Start(psi)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
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

    // ---- Destination-collision guard (TASK-229) ----
    //
    // PreallocatedFile opens the output file with FileShare.ReadWrite (needed for one download's own
    // concurrent segment workers), which does nothing to stop a second, unrelated download from also opening
    // and writing that same path. StartAsync now refuses to start a download whose destination is already
    // claimed by another Active row, rather than let two engines write the same file independently.

    /// <summary>
    /// Starts a real (slow-but-genuine) download and waits until it is observed Active in the repository —
    /// the "another download is already active here" fixture every collision test below needs. The caller
    /// must await the returned task before the test ends so the transfer completes and releases its file
    /// handle before the temp directory is torn down.
    /// </summary>
    private async Task<(long Id, Task Run)> StartActiveAsync(Uri url, string directory, string fileName)
    {
        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = url,
            DestinationDirectory = directory,
            FileName = fileName,
            MaxConnections = 1,
        });

        var activeSeen = new TaskCompletionSource();
        void OnStatusChanged(object? sender, DownloadStatusChangedEventArgs e)
        {
            if (e.DownloadId == id && e.Current == DownloadStatus.Active)
            {
                activeSeen.TrySetResult();
            }
        }

        Manager.StatusChanged += OnStatusChanged;
        Task run = Manager.StartAsync(id);
        await activeSeen.Task;
        Manager.StatusChanged -= OnStatusChanged;
        return (id, run);
    }

    [Fact]
    public async Task StartAsync_Queued_BlockedByActiveCollisionAtSameDestination_FailsVisibly()
    {
        await using var server = new LoopbackHttpServer
        {
            Body = Bytes(32 * 1024),
            SupportRanges = true,
            ResponseDelay = TimeSpan.FromMilliseconds(400), // holds it Active long enough to race the collision
        };
        (long activeId, Task activeRun) = await StartActiveAsync(server.Url("file.bin"), _tempDir, "shared.bin");

        long queuedId = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "shared.bin",
        });

        Func<Task> act = () => Manager.StartAsync(queuedId);
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*{activeId}*");

        Download? blocked = await Repository.GetAsync(queuedId);
        blocked!.Status.Should().Be(DownloadStatusCodes.Failed);
        blocked.Error.Should().Contain("already being downloaded");

        await activeRun;
    }

    [Fact]
    public async Task StartAsync_Paused_ResumeBlockedByActiveCollisionAtSameDestination_FailsVisibly()
    {
        // Reach Paused for the first row via a real pause (cancel mid-transfer), matching PauseResumeTests.
        byte[] pausedBody = Bytes(256 * 1024);
        await using var pausedServer = new LoopbackHttpServer
        {
            Body = pausedBody,
            SupportRanges = true,
            SlowTailFrom = 128 * 1024,
            SlowTailDelay = TimeSpan.FromMilliseconds(600),
        };
        long pausedId = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = pausedServer.Url("file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "shared.bin",
            MaxConnections = 4,
        });

        using var pauseCts = new CancellationTokenSource();
        int cancelled = 0;
        Manager.ProgressChanged += (_, e) =>
        {
            if (e.DownloadId == pausedId && e.Progress.DownloadedBytes >= 64 * 1024 &&
                Interlocked.Exchange(ref cancelled, 1) == 0)
            {
                pauseCts.Cancel();
            }
        };
        Func<Task> pause = () => Manager.StartAsync(pausedId, pauseCts.Token);
        await pause.Should().ThrowAsync<OperationCanceledException>();
        (await Repository.GetAsync(pausedId))!.Status.Should().Be(DownloadStatusCodes.Paused); // sanity

        // Now another download claims the same destination — the resume attempt below must be blocked.
        await using var activeServer = new LoopbackHttpServer
        {
            Body = Bytes(32 * 1024),
            SupportRanges = true,
            ResponseDelay = TimeSpan.FromMilliseconds(400),
        };
        (long activeId, Task activeRun) =
            await StartActiveAsync(activeServer.Url("file.bin"), _tempDir, "shared.bin");

        Func<Task> resume = () => Manager.StartAsync(pausedId);
        (await resume.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*{activeId}*");

        (await Repository.GetAsync(pausedId))!.Status.Should().Be(DownloadStatusCodes.Failed);

        await activeRun;
    }

    [Fact]
    public async Task StartAsync_Failed_RetryBlockedByActiveCollisionAtSameDestination_RefreshesErrorInPlace()
    {
        // The .invalid TLD never resolves (RFC 2606), so this fails deterministically with no network.
        long failedId = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = new Uri("http://nonexistent.invalid/file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "shared.bin",
        });
        Func<Task> firstAttempt = () => Manager.StartAsync(failedId);
        await firstAttempt.Should().ThrowAsync<Exception>();
        (await Repository.GetAsync(failedId))!.Status.Should().Be(DownloadStatusCodes.Failed); // sanity

        await using var activeServer = new LoopbackHttpServer
        {
            Body = Bytes(32 * 1024),
            SupportRanges = true,
            ResponseDelay = TimeSpan.FromMilliseconds(400),
        };
        (long activeId, Task activeRun) =
            await StartActiveAsync(activeServer.Url("file.bin"), _tempDir, "shared.bin");

        Func<Task> retry = () => Manager.StartAsync(failedId);
        (await retry.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*{activeId}*");

        Download? afterRetry = await Repository.GetAsync(failedId);
        afterRetry!.Status.Should().Be(
            DownloadStatusCodes.Failed, "Failed has no legal self-transition, so the status is left as-is");
        afterRetry.Error.Should().Contain(
            "already being downloaded", "the reason is refreshed in place even without a status change");

        await activeRun;
    }

    [Fact]
    public async Task StartAsync_Expired_RetryBlockedByActiveCollisionAtSameDestination_RefreshesErrorInPlace()
    {
        // Every response (including the pre-flight probe) answers 403 — a conventional expired-link status
        // (ExpiryDetection.IsExpiryStatusCode) — so the first attempt lands on Expired deterministically.
        await using var expiredServer = new LoopbackHttpServer { StatusOverride = 403 };
        long expiredId = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = expiredServer.Url("file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "shared.bin",
        });
        Func<Task> firstAttempt = () => Manager.StartAsync(expiredId);
        await firstAttempt.Should().ThrowAsync<Exception>();
        (await Repository.GetAsync(expiredId))!.Status.Should().Be(DownloadStatusCodes.Expired); // sanity

        await using var activeServer = new LoopbackHttpServer
        {
            Body = Bytes(32 * 1024),
            SupportRanges = true,
            ResponseDelay = TimeSpan.FromMilliseconds(400),
        };
        (long activeId, Task activeRun) =
            await StartActiveAsync(activeServer.Url("file.bin"), _tempDir, "shared.bin");

        Func<Task> retry = () => Manager.StartAsync(expiredId);
        (await retry.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*{activeId}*");

        Download? afterRetry = await Repository.GetAsync(expiredId);
        afterRetry!.Status.Should().Be(
            DownloadStatusCodes.Expired, "Expired has no legal transition to Failed, so the status is left as-is");
        afterRetry.Error.Should().Contain("already being downloaded");

        await activeRun;
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
