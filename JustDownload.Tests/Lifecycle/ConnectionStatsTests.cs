using System.Collections.Concurrent;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Data;
using JustDownload.Core.Data.Migrations;
using JustDownload.Core.Downloading;
using JustDownload.Core.Lifecycle;
using JustDownload.Tests.Transport;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>
/// Integration tests that the download manager exposes live per-connection stats (TASK-054) end to end: a
/// multi-connection download reports several connections via <see cref="IDownloadManager.GetConnections"/>
/// while active, and clears them once it leaves the active state.
/// </summary>
public sealed class ConnectionStatsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ServiceProvider _provider;

    public ConnectionStatsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-connstats-" + Guid.NewGuid().ToString("N"));
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
        _provider = services.BuildServiceProvider();
        _provider.GetRequiredService<IMigrationRunner>().Migrate();
    }

    private IDownloadManager Manager => _provider.GetRequiredService<IDownloadManager>();

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
    public async Task MultiConnectionDownload_ExposesLiveConnections_ThenClearsOnCompletion()
    {
        // A 2 MiB body over 64 KiB copy buffers means each of the four ~512 KiB segments spans many writes,
        // so connections report repeatedly and overlap — making the live multi-connection view deterministic.
        // A speed limit gives the transfer a guaranteed multi-second floor (TASK-030's token bucket) so the
        // 2ms poll loop below can never race a fast loopback + fast CI runner to completion before observing
        // more than one connection (previously unthrottled, this flaked on a fast windows-latest runner).
        byte[] body = Bytes(2 * 1024 * 1024);
        await using var server = new LoopbackHttpServer
        {
            Body = body,
            SupportRanges = true,
            ResponseDelay = TimeSpan.FromMilliseconds(20),
        };

        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "conn.bin",
            MaxConnections = 4,
            SpeedLimit = 512 * 1024,
        });

        var idsSeen = new ConcurrentDictionary<int, byte>();
        var rangesSeen = new ConcurrentBag<long>();

        // Poll the live connection stats directly while the download runs, rather than piggy-backing on the
        // progress event: progress notifications are coalesced to ~15Hz (TASK-104), too sparse to reliably
        // sample a fast transfer. This observes the same thing the detail view shows (TASK-054), just on its
        // own cadence so the test does not depend on the UI notification rate.
        Task<DownloadResult> download = Manager.StartAsync(id);
        while (!download.IsCompleted)
        {
            foreach (ConnectionStat s in Manager.GetConnections(id))
            {
                idsSeen[s.ConnectionId] = 1;
                rangesSeen.Add(s.End - s.Start + 1);
            }

            await Task.Delay(2);
        }

        await download;

        idsSeen.Count.Should().BeGreaterThan(1, "a multi-connection download surfaces several distinct connections");
        rangesSeen.Should().OnlyContain(length => length > 0, "each connection works a non-empty byte range");

        // Once complete the live connection stats are cleared (the detail view's Connections tab empties).
        Manager.GetConnections(id).Should().BeEmpty();
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
