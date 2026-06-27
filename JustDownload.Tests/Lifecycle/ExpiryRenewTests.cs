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
/// Integration tests for expiry detection and the renew flow (TASK-032, US-13) over a temp database and
/// loopback server: a 403 link surfaces as Expired (AC0); a renew with a same-size URL resumes from the kept
/// checkpoint without re-fetching (AC1); a renew with a different resource clears the checkpoint and restarts
/// cleanly, reproducing the new content (AC2).
/// </summary>
public sealed class ExpiryRenewTests : IDisposable
{
    private const int FileSize = 1024 * 1024;

    private readonly string _tempDir;
    private readonly ServiceProvider _provider;

    public ExpiryRenewTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-renew-" + Guid.NewGuid().ToString("N"));
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

    private IDownloadRepository Downloads => _provider.GetRequiredService<IDownloadRepository>();

    private ISegmentRepository Segments => _provider.GetRequiredService<ISegmentRepository>();

    private static byte[] Bytes(int count, int seed)
    {
        var data = new byte[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = (byte)((i * 31 + seed) % 256);
        }

        return data;
    }

    private async Task<long> DownloadUntilPausedAsync(LoopbackHttpServer server, string fileName, long cancelAt)
    {
        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationDirectory = _tempDir,
            FileName = fileName,
            MaxConnections = 4,
        });

        using var cts = new CancellationTokenSource();
        int cancelled = 0;
        Manager.ProgressChanged += (_, e) =>
        {
            if (e.DownloadId == id && e.Progress.DownloadedBytes >= cancelAt &&
                Interlocked.Exchange(ref cancelled, 1) == 0)
            {
                cts.Cancel();
            }
        };

        try
        {
            await Manager.StartAsync(id, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        return id;
    }

    [Fact]
    public async Task ExpiredLink_SurfacesAsExpired()
    {
        await using var server = new LoopbackHttpServer { Body = Bytes(1024, 1), StatusOverride = 403 };

        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "gone.bin",
        });

        Func<Task> act = async () => await Manager.StartAsync(id);
        await act.Should().ThrowAsync<DownloadExpiredException>();

        (await Downloads.GetAsync(id))!.Status.Should().Be(DownloadStatusCodes.Expired);
    }

    [Fact]
    public async Task Renew_WithMatchingResource_ResumesWithoutRefetch()
    {
        byte[] body = Bytes(FileSize, 7);
        await using var server = new LoopbackHttpServer
        {
            Body = body,
            SupportRanges = true,
            SlowTailFrom = FileSize / 2,
            SlowTailDelay = TimeSpan.FromMilliseconds(600),
        };

        long id = await DownloadUntilPausedAsync(server, "renew-match.bin", 256 * 1024);
        IReadOnlyList<DownloadSegment> checkpoint = await Segments.GetByDownloadAsync(id);
        checkpoint.Should().NotBeEmpty();

        // Simulate the link having expired, then renew with a fresh URL serving the identical body (same size).
        await Downloads.UpdateAsync((await Downloads.GetAsync(id))! with { Status = DownloadStatusCodes.Expired });
        server.ClearServedRanges();

        DownloadResult result = await Manager.RenewAsync(id, server.Url("renewed.bin"));

        result.TotalBytes.Should().Be(FileSize);
        (await File.ReadAllBytesAsync(Path.Combine(_tempDir, "renew-match.bin"))).Should().Equal(body);
        (await Downloads.GetAsync(id))!.Status.Should().Be(DownloadStatusCodes.Completed);

        // No already-downloaded byte was re-served (only the 1-byte range probe may touch the head).
        long refetched = 0;
        foreach ((long from, long to) in server.ServedRanges)
        {
            foreach (DownloadSegment had in checkpoint)
            {
                long lo = Math.Max(from, had.Start);
                long hi = Math.Min(to, had.End);
                if (hi >= lo)
                {
                    refetched += hi - lo + 1;
                }
            }
        }

        refetched.Should().BeLessThanOrEqualTo(1024, "a matching renew resumes; it must not re-fetch held bytes");
    }

    [Fact]
    public async Task Renew_WithDifferentResource_ClearsCheckpoint_AndRestarts()
    {
        byte[] original = Bytes(FileSize, 7);
        await using var first = new LoopbackHttpServer
        {
            Body = original,
            SupportRanges = true,
            SlowTailFrom = FileSize / 2,
            SlowTailDelay = TimeSpan.FromMilliseconds(600),
        };

        long id = await DownloadUntilPausedAsync(first, "renew-diff.bin", 256 * 1024);
        (await Segments.GetByDownloadAsync(id)).Should().NotBeEmpty();
        await Downloads.UpdateAsync((await Downloads.GetAsync(id))! with { Status = DownloadStatusCodes.Expired });

        // The replacement points at a DIFFERENT resource (different size) — identity cannot match.
        byte[] replacement = Bytes(FileSize + (128 * 1024), 99);
        await using var second = new LoopbackHttpServer { Body = replacement, SupportRanges = true };

        DownloadResult result = await Manager.RenewAsync(id, second.Url("other.bin"));

        result.TotalBytes.Should().Be(replacement.Length);
        (await File.ReadAllBytesAsync(Path.Combine(_tempDir, "renew-diff.bin"))).Should().Equal(replacement);
        (await Downloads.GetAsync(id))!.Status.Should().Be(DownloadStatusCodes.Completed);
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
