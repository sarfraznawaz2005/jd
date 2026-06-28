using System.Diagnostics;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Downloading;
using JustDownload.Tests.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JustDownload.Tests.Downloading;

/// <summary>
/// Live connection-count control over the real engine (TASK-027, US-4): raising the desired count spawns
/// more connections mid-download without restarting (AC0), lowering it drains connections at boundaries
/// while the download still finishes byte-correct (AC1), and the active count is reflected through the
/// controller's state and events (AC2). A per-download speed limit keeps the transfer in flight long enough
/// to adjust it deterministically.
/// </summary>
public sealed class LiveConnectionControlTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "jd-live-" + Guid.NewGuid().ToString("N"));

    public LiveConnectionControlTests() => Directory.CreateDirectory(_tempDir);

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
        // Small floors so a modest body splits into many segments and can be freely stolen for new workers.
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

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    [Fact]
    public async Task RaisingConnections_SpawnsMoreWorkers_WithoutRestart()
    {
        byte[] body = Bytes(1024 * 1024);
        await using var server = new LoopbackHttpServer { Body = body, SupportRanges = true };
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();

        var controller = new ConnectionController(2);
        string dest = Dest("raise.bin");
        var task = downloader.DownloadAsync(
            new DownloadRequest
            {
                Url = server.Url("f.bin"),
                DestinationPath = dest,
                Connections = 2,
                SpeedLimit = 512 * 1024, // ~2s so we can adjust mid-flight
            },
            connections: controller);

        (await WaitUntilAsync(() => controller.ActiveConnections >= 2, TimeSpan.FromSeconds(5)))
            .Should().BeTrue("the two initial connections should start");

        int activeBeforeRaise = controller.ActiveConnections;
        controller.SetDesiredConnections(6);

        bool grew = await WaitUntilAsync(() => controller.ActiveConnections > activeBeforeRaise, TimeSpan.FromSeconds(5));

        await task;

        grew.Should().BeTrue("raising the desired count spawns more connections mid-download");
        (await File.ReadAllBytesAsync(dest)).Should().Equal(body);
        controller.ActiveConnections.Should().Be(0, "all connections retire when the download completes");
    }

    [Fact]
    public async Task LoweringConnections_DrainsCleanly_AndStillCompletesCorrectly()
    {
        byte[] body = Bytes(1024 * 1024);
        await using var server = new LoopbackHttpServer { Body = body, SupportRanges = true };
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();

        var controller = new ConnectionController(6);
        string dest = Dest("lower.bin");
        var task = downloader.DownloadAsync(
            new DownloadRequest
            {
                Url = server.Url("f.bin"),
                DestinationPath = dest,
                Connections = 6,
                SpeedLimit = 512 * 1024,
            },
            connections: controller);

        (await WaitUntilAsync(() => controller.ActiveConnections >= 4, TimeSpan.FromSeconds(5)))
            .Should().BeTrue("several initial connections should start");

        controller.SetDesiredConnections(2);
        bool drained = await WaitUntilAsync(() => controller.ActiveConnections <= 2, TimeSpan.FromSeconds(5));

        await task;

        drained.Should().BeTrue("lowering the desired count drains connections at segment boundaries");
        (await File.ReadAllBytesAsync(dest)).Should().Equal(body, "a clean drain never corrupts the output");
        controller.ActiveConnections.Should().Be(0);
    }

    [Fact]
    public async Task ActiveCount_IsReflectedInEvents()
    {
        byte[] body = Bytes(512 * 1024);
        await using var server = new LoopbackHttpServer { Body = body, SupportRanges = true };
        using ServiceProvider provider = BuildProvider();
        var downloader = provider.GetRequiredService<ISegmentedDownloader>();

        var controller = new ConnectionController(4);
        var activeReports = new List<int>();
        controller.Changed += (_, e) =>
        {
            lock (activeReports)
            {
                activeReports.Add(e.ActiveConnections);
            }
        };

        await downloader.DownloadAsync(
            new DownloadRequest { Url = server.Url("f.bin"), DestinationPath = Dest("events.bin"), Connections = 4 },
            connections: controller);

        lock (activeReports)
        {
            activeReports.Should().NotBeEmpty("the engine reports active-count changes");
            activeReports.Should().Contain(c => c > 0, "connections become active");
            activeReports[^1].Should().Be(0, "the final report is zero active connections");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
