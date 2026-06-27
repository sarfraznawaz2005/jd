using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Data;
using JustDownload.Core.Data.Migrations;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Downloading;
using JustDownload.Core.Lifecycle;
using JustDownload.Tests.Transport;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>
/// Integration tests for the live status grouping (TASK-045 AC1) over a temp database and loopback server:
/// a freshly enqueued download lands in Incomplete and moves to Completed when it finishes, with
/// <see cref="IDownloadStatusGroups.Changed"/> raised across the transition; <see cref="IDownloadStatusGroups.RefreshAsync"/>
/// seeds membership from persisted records.
/// </summary>
public sealed class DownloadStatusGroupTrackerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ServiceProvider _provider;

    public DownloadStatusGroupTrackerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-grp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var pathProvider = Substitute.For<IDatabasePathProvider>();
        pathProvider.DatabaseDirectory.Returns(_tempDir);
        pathProvider.DatabasePath.Returns(Path.Combine(_tempDir, "test.db"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(pathProvider);
        services.AddSingleton(new SegmentationOptions { DefaultConnections = 4, MinSegmentSize = 16 * 1024 });
        services.AddJustDownloadData();
        services.AddJustDownloadTransport();
        services.AddJustDownloadDownloading();
        services.AddJustDownloadLifecycle();
        _provider = services.BuildServiceProvider();
        _provider.GetRequiredService<IMigrationRunner>().Migrate();
    }

    private IDownloadManager Manager => _provider.GetRequiredService<IDownloadManager>();

    private IDownloadStatusGroups Groups => _provider.GetRequiredService<IDownloadStatusGroups>();

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
    public async Task Membership_MovesFromIncompleteToCompleted_AsDownloadFinishes()
    {
        byte[] body = Bytes(128 * 1024);
        await using var server = new LoopbackHttpServer { Body = body, SupportRanges = true };

        IDownloadStatusGroups groups = Groups; // resolve once so it subscribes before we enqueue
        int changes = 0;
        groups.Changed += (_, _) => Interlocked.Increment(ref changes);

        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "grp.bin",
        });

        // Enqueue → Incomplete.
        groups.Count(DownloadStatusGroup.Incomplete).Should().Be(1);
        groups.Count(DownloadStatusGroup.Completed).Should().Be(0);
        groups.Ids(DownloadStatusGroup.Incomplete).Should().ContainSingle().Which.Should().Be(id);

        await Manager.StartAsync(id);

        // Completion → Completed; Incomplete now empty.
        groups.Count(DownloadStatusGroup.Completed).Should().Be(1);
        groups.Count(DownloadStatusGroup.Incomplete).Should().Be(0);
        groups.Ids(DownloadStatusGroup.Completed).Should().ContainSingle().Which.Should().Be(id);

        // Changed fired live for the enqueue and the completion (active→complete moved the bucket).
        changes.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task RefreshAsync_SeedsFromPersistedRecords()
    {
        await Repository.AddAsync(new Download
        {
            Url = "https://example.com/done.bin",
            Status = DownloadStatusCodes.Completed,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await Repository.AddAsync(new Download
        {
            Url = "https://example.com/wip.bin",
            Status = DownloadStatusCodes.Paused,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await Groups.RefreshAsync();

        Groups.Count(DownloadStatusGroup.Completed).Should().Be(1);
        Groups.Count(DownloadStatusGroup.Incomplete).Should().Be(1);
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
