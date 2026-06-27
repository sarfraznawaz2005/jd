using System.Diagnostics;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Downloading;
using JustDownload.Core.Throttling;
using JustDownload.Tests.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JustDownload.Tests.Throttling;

/// <summary>
/// Integration tests that the throttle actually limits a real download (TASK-030): the global cap and a
/// per-download cap each slow the transfer (AC0), while unlimited stays fast (AC2). Times are asserted
/// with generous margins to avoid flakiness — an unthrottled loopback download finishes in well under
/// 100 ms, so a multi-hundred-ms floor cleanly distinguishes "throttled" from "not".
/// </summary>
public sealed class DownloadThrottleTests : IDisposable
{
    private readonly string _tempDir;

    public DownloadThrottleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-throttle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private string Dest(string name) => Path.Combine(_tempDir, name);

    private static byte[] Bytes(int count)
    {
        var data = new byte[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = (byte)(i % 251);
        }

        return data;
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
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
    public async Task GlobalCap_ThrottlesAggregateThroughput()
    {
        // AC0: a global cap slows the whole (multi-connection) download.
        byte[] body = Bytes(256 * 1024);
        await using var server = new LoopbackHttpServer { Body = body, SupportRanges = true };
        using ServiceProvider provider = BuildProvider();
        provider.GetRequiredService<IRateLimiter>().BytesPerSecond = 256 * 1024; // ~1s for 256 KiB

        var downloader = provider.GetRequiredService<ISegmentedDownloader>();
        var stopwatch = Stopwatch.StartNew();
        await downloader.DownloadAsync(new DownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationPath = Dest("global.bin"),
            Connections = 4,
        });
        stopwatch.Stop();

        stopwatch.ElapsedMilliseconds.Should().BeGreaterThan(600, "256 KiB at 256 KiB/s should take about a second");
        (await File.ReadAllBytesAsync(Dest("global.bin"))).Should().Equal(body);
    }

    [Fact]
    public async Task PerDownloadCap_ThrottlesTransfer()
    {
        // AC0: a per-download cap (with no global cap) slows just that download.
        byte[] body = Bytes(128 * 1024);
        await using var server = new LoopbackHttpServer { Body = body, SupportRanges = true };
        using ServiceProvider provider = BuildProvider();

        var downloader = provider.GetRequiredService<ISegmentedDownloader>();
        var stopwatch = Stopwatch.StartNew();
        await downloader.DownloadAsync(new DownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationPath = Dest("perdl.bin"),
            Connections = 4,
            SpeedLimit = 128 * 1024, // ~1s for 128 KiB
        });
        stopwatch.Stop();

        stopwatch.ElapsedMilliseconds.Should().BeGreaterThan(500);
        (await File.ReadAllBytesAsync(Dest("perdl.bin"))).Should().Equal(body);
    }

    [Fact]
    public async Task Unlimited_IsFast()
    {
        // AC2: with no caps, throttling is out of the way (well under the throttled floor).
        byte[] body = Bytes(256 * 1024);
        await using var server = new LoopbackHttpServer { Body = body, SupportRanges = true };
        using ServiceProvider provider = BuildProvider();

        var downloader = provider.GetRequiredService<ISegmentedDownloader>();
        var stopwatch = Stopwatch.StartNew();
        await downloader.DownloadAsync(new DownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationPath = Dest("fast.bin"),
            Connections = 4,
        });
        stopwatch.Stop();

        stopwatch.ElapsedMilliseconds.Should().BeLessThan(400, "an uncapped loopback download is near-instant");
        (await File.ReadAllBytesAsync(Dest("fast.bin"))).Should().Equal(body);
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
