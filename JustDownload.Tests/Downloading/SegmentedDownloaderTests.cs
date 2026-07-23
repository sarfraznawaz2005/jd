using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Downloading;
using JustDownload.Tests.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JustDownload.Tests.Downloading;

/// <summary>
/// Integration tests for <see cref="SegmentedDownloader"/> over a real loopback HTTP server (TASK-026):
/// a multi-connection download writes the correct bytes and uses all connections in parallel (AC0/AC3),
/// a range-less server falls back to one connection (AC0), and a slow tail provokes a work-steal that
/// still produces a correct file (AC1).
/// </summary>
public sealed class SegmentedDownloaderTests : IDisposable
{
    private readonly string _tempDir;

    public SegmentedDownloaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-seg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private string Dest(string name) => Path.Combine(_tempDir, name);

    private static byte[] Bytes(int count)
    {
        var data = new byte[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = (byte)((i * 31 + 7) % 256);
        }

        return data;
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Small floors so modest test bodies still split into several segments and can be stolen.
        services.AddSingleton(new SegmentationOptions
        {
            DefaultConnections = 4,
            MinSegmentSize = 16 * 1024,
            MinStealSize = 16 * 1024,
        });
        services.AddJustDownloadTransport();
        services.AddJustDownloadDownloading();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task MultiConnectionDownload_WritesCorrectFile_UsingAllConnections()
    {
        // AC0 + AC3: 4 connections, all active at once, produce a byte-exact file.
        byte[] body = Bytes(256 * 1024);
        await using var server = new LoopbackHttpServer
        {
            Body = body,
            SupportRanges = true,
            ResponseDelay = TimeSpan.FromMilliseconds(120), // overlap the connections so the peak is observable
        };
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();
        string dest = Dest("multi.bin");

        DownloadResult result = await downloader.DownloadAsync(new DownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationPath = dest,
            Connections = 4,
        });

        result.SingleConnection.Should().BeFalse();
        result.InitialSegments.Should().Be(4);
        result.TotalBytes.Should().Be(body.Length);
        server.MaxConcurrentConnections.Should().Be(4, "all four connections should download in parallel");
        (await File.ReadAllBytesAsync(dest)).Should().Equal(body);
    }

    [Fact]
    public async Task RangeLessServer_FallsBackToSingleConnection_AndWritesCorrectFile()
    {
        // AC0: no range support → one connection, still byte-exact.
        byte[] body = Bytes(100 * 1024);
        await using var server = new LoopbackHttpServer { Body = body, SupportRanges = false };
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();
        string dest = Dest("single.bin");

        DownloadResult result = await downloader.DownloadAsync(new DownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationPath = dest,
            Connections = 4,
        });

        result.SingleConnection.Should().BeTrue();
        result.InitialSegments.Should().Be(1);
        result.Steals.Should().Be(0);
        (await File.ReadAllBytesAsync(dest)).Should().Equal(body);
    }

    [Fact]
    public async Task WorkStealing_OccursWhenOneSegmentIsSlow_AndFileIsStillCorrect()
    {
        // AC1: with 2 connections and a slow second half, the fast connection steals the tail.
        byte[] body = Bytes(256 * 1024);
        await using var server = new LoopbackHttpServer
        {
            Body = body,
            SupportRanges = true,
            SlowTailFrom = 128 * 1024,                  // the second segment (and any steal of it) is slow
            SlowTailDelay = TimeSpan.FromMilliseconds(400),
        };
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();
        string dest = Dest("steal.bin");

        DownloadResult result = await downloader.DownloadAsync(new DownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationPath = dest,
            Connections = 2,
        });

        result.InitialSegments.Should().Be(2);
        result.Steals.Should().BeGreaterThan(0, "the fast connection should re-split the slow segment's tail");
        (await File.ReadAllBytesAsync(dest)).Should().Equal(body);
    }

    [Fact]
    public async Task Download_ReportsCumulativeProgress()
    {
        byte[] body = Bytes(128 * 1024);
        await using var server = new LoopbackHttpServer { Body = body, SupportRanges = true };
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();

        // Progress<T> callbacks run on the thread pool out of order, so track the high-water mark
        // (the cumulative total is monotonic) rather than whichever callback happens to run last.
        long maxReported = 0;
        var progress = new Progress<long>(v =>
        {
            long current;
            do
            {
                current = Interlocked.Read(ref maxReported);
                if (v <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref maxReported, v, current) != current);
        });

        await downloader.DownloadAsync(
            new DownloadRequest { Url = server.Url("file.bin"), DestinationPath = Dest("prog.bin"), Connections = 4 },
            progress);

        // Let any queued progress callbacks drain, then the high-water mark must equal the file size
        // (and never exceed it — which would mean overlapping/double-counted writes).
        await Task.Delay(100);
        Interlocked.Read(ref maxReported).Should().Be(body.Length);
    }

    /// <summary>Builds a provider with a short, deterministic idle-read timeout and small segmentation floors
    /// so a modest test body still splits when multiple connections are requested.</summary>
    private static ServiceProvider BuildStallTestProvider(int defaultConnections) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton(new SegmentationOptions
            {
                DefaultConnections = defaultConnections,
                MinSegmentSize = 16 * 1024,
                MinStealSize = 16 * 1024,
                IdleReadTimeout = TimeSpan.FromMilliseconds(200),
            })
            .AddJustDownloadTransport()
            .AddJustDownloadDownloading()
            .BuildServiceProvider();

    [Fact]
    public async Task SingleConnectionDownload_StalledConnection_FailsPromptly_RatherThanHangingForever()
    {
        // A connection that stays open but stops sending bytes has no timeout of its own — HttpClient.Timeout
        // is deliberately infinite for large transfers — so without an idle-read watchdog this would hang
        // until the caller gives up. Stall for far longer than the configured idle timeout so a passing test
        // proves the watchdog fired, not that the transfer happened to finish just in time. Connections=1
        // routes through DownloadSingleAsync/ThrottledCopyAsync — the single-connection counterpart of the
        // segmented path's guard, exercised separately below.
        byte[] body = Bytes(64 * 1024);
        await using var server = new LoopbackHttpServer
        {
            Body = body,
            SupportRanges = true,
            StallAfterBytes = 8 * 1024,
            StallDuration = TimeSpan.FromSeconds(10),
        };
        using ServiceProvider provider = BuildStallTestProvider(defaultConnections: 1);
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();

        // A bounded outer cancellation is a safety net for the test itself, not the mechanism under test: if
        // the idle-read watchdog were broken/removed, this would fail with OperationCanceledException instead
        // of the expected IOException — a clear test failure — rather than hanging the suite for 10s+.
        using var safetyNet = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Func<Task> act = () => downloader.DownloadAsync(
            new DownloadRequest { Url = server.Url("file.bin"), DestinationPath = Dest("stalled-single.bin"), Connections = 1 },
            cancellationToken: safetyNet.Token);

        (await act.Should().ThrowAsync<IOException>())
            .WithMessage("*stalled*");
    }

    [Fact]
    public async Task SegmentedDownload_StalledConnection_FailsPromptly_RatherThanHangingForever()
    {
        // Same protection, the multi-connection path: PumpAsync's per-segment idle-read guard, not
        // ThrottledCopyAsync's. Every segment's response exceeds StallAfterBytes, so every worker stalls —
        // the point is proving the download fails promptly rather than hanging, not which segment first does.
        byte[] body = Bytes(64 * 1024);
        await using var server = new LoopbackHttpServer
        {
            Body = body,
            SupportRanges = true,
            StallAfterBytes = 4 * 1024,
            StallDuration = TimeSpan.FromSeconds(10),
        };
        using ServiceProvider provider = BuildStallTestProvider(defaultConnections: 2);
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();
        using var safetyNet = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Func<Task> act = () => downloader.DownloadAsync(
            new DownloadRequest { Url = server.Url("file.bin"), DestinationPath = Dest("stalled-segmented.bin"), Connections = 2 },
            cancellationToken: safetyNet.Token);

        (await act.Should().ThrowAsync<IOException>())
            .WithMessage("*stalled*");
    }

    public void Dispose()
    {
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
