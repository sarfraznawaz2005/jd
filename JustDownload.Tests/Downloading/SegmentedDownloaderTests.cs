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
            ResponseDelay = TimeSpan.FromMilliseconds(60), // overlap the connections so the peak is observable
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

        long last = 0;
        var progress = new Progress<long>(v => Interlocked.Exchange(ref last, v));

        await downloader.DownloadAsync(
            new DownloadRequest { Url = server.Url("file.bin"), DestinationPath = Dest("prog.bin"), Connections = 4 },
            progress);

        // Allow the last posted progress callback to run.
        await Task.Delay(50);
        Interlocked.Read(ref last).Should().Be(body.Length);
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
