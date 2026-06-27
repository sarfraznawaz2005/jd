using System.Security.Cryptography;
using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Data;
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
/// Crash-recovery and resume-integrity tests (TASK-029, US-2 / KPI K5). A download is interrupted mid-flight,
/// its checkpoint is rolled back to simulate a kill where the last in-flight writes were not yet
/// checkpointed, and a fresh service provider (a new "process") runs the startup recovery scan and resumes —
/// the final file must be SHA-256-identical to the reference (AC0). A server that stops honoring the resume
/// offset surfaces a restart-required failure and clears the stale checkpoint (AC1). The startup scan demotes
/// interrupted (still-active) downloads to resumable (AC2).
/// </summary>
public sealed class CrashRecoveryTests : IDisposable
{
    private const int FileSize = 1024 * 1024;

    private readonly string _tempDir;
    private readonly IDatabasePathProvider _pathProvider;

    public CrashRecoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _pathProvider = Substitute.For<IDatabasePathProvider>();
        _pathProvider.DatabaseDirectory.Returns(_tempDir);
        _pathProvider.DatabasePath.Returns(Path.Combine(_tempDir, "test.db"));
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_pathProvider);
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
        // Categorization is part of the init seam (override application); include the full core so
        // InitializeJustDownloadCoreAsync resolves everything.
        services.AddJustDownloadCategorization();
        services.AddJustDownloadSecrets();
        return services.BuildServiceProvider();
    }

    private static byte[] Bytes(int count)
    {
        var data = new byte[count];
        for (int i = 0; i < count; i++)
        {
            data[i] = (byte)((i * 131 + 17) % 256);
        }

        return data;
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static LoopbackHttpServer FastHeadSlowTailServer(byte[] body) => new()
    {
        Body = body,
        SupportRanges = true,
        SlowTailFrom = FileSize / 2,
        SlowTailDelay = TimeSpan.FromMilliseconds(600),
    };

    // Drops `lag` bytes off the largest persisted interval, simulating a checkpoint that lagged the disk at
    // the moment of the kill (the file holds those bytes; the checkpoint forgot them).
    private static async Task RollBackCheckpointAsync(ISegmentRepository segments, long id, long lag)
    {
        IReadOnlyList<DownloadSegment> rows = await segments.GetByDownloadAsync(id);
        if (rows.Count == 0)
        {
            return;
        }

        DownloadSegment biggest = rows.MaxBy(r => r.End - r.Start)!;
        long trim = Math.Min(lag, biggest.Downloaded - 1);
        if (trim <= 0)
        {
            return;
        }

        await segments.UpdateAsync(biggest with { End = biggest.End - trim, Downloaded = biggest.Downloaded - trim });
    }

    private async Task<long> DownloadUntilAsync(
        ServiceProvider provider, LoopbackHttpServer server, string fileName, long cancelAt)
    {
        var manager = provider.GetRequiredService<IDownloadManager>();
        long id = await manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationDirectory = _tempDir,
            FileName = fileName,
            MaxConnections = 4,
        });

        using var cts = new CancellationTokenSource();
        int cancelled = 0;
        manager.ProgressChanged += (_, e) =>
        {
            if (e.DownloadId == id && e.Progress.DownloadedBytes >= cancelAt &&
                Interlocked.Exchange(ref cancelled, 1) == 0)
            {
                cts.Cancel();
            }
        };

        try
        {
            await manager.StartAsync(id, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        return id;
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    public async Task KillAtOffset_ThenRecoverAndResume_YieldsSha256IdenticalFile(double crashFraction)
    {
        byte[] body = Bytes(FileSize);
        string expectedSha = Sha(body);
        await using LoopbackHttpServer server = FastHeadSlowTailServer(body);
        string fileName = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"crash-{crashFraction}.bin");

        // --- Process 1: download partway, then "crash". ---
        long id;
        using (ServiceProvider a = BuildProvider())
        {
            await a.InitializeJustDownloadCoreAsync();
            id = await DownloadUntilAsync(a, server, fileName, (long)(FileSize * crashFraction));

            IDownloadRepository downloadsA = a.GetRequiredService<IDownloadRepository>();
            Download rec = (await downloadsA.GetAsync(id))!;

            // Simulate kill -9: the process never ran a clean pause, so the row is still "active"; and the
            // periodic checkpoint lagged the disk by some in-flight bytes.
            await downloadsA.UpdateAsync(rec with { Status = DownloadStatusCodes.Active });
            await RollBackCheckpointAsync(a.GetRequiredService<ISegmentRepository>(), id, 48 * 1024);
        }

        // --- Process 2: fresh provider over the same DB + file. ---
        using ServiceProvider b = BuildProvider();
        await b.InitializeJustDownloadCoreAsync(); // recovery scan demotes the active download to paused

        IDownloadRepository downloadsB = b.GetRequiredService<IDownloadRepository>();
        (await downloadsB.GetAsync(id))!.Status.Should().Be(
            DownloadStatusCodes.Paused, "the startup recovery scan should offer the interrupted download for resume");

        await b.GetRequiredService<IDownloadManager>().StartAsync(id);

        byte[] finalBytes = await File.ReadAllBytesAsync(Path.Combine(_tempDir, fileName));
        Sha(finalBytes).Should().Be(expectedSha, "the resumed file must be byte-identical to the reference");
        (await downloadsB.GetAsync(id))!.Status.Should().Be(DownloadStatusCodes.Completed);
    }

    [Fact]
    public async Task Resume_WhenServerRejectsOffset_SurfacesRestartRequired_AndClearsCheckpoint()
    {
        byte[] body = Bytes(FileSize);
        await using LoopbackHttpServer server = FastHeadSlowTailServer(body);

        using ServiceProvider provider = BuildProvider();
        await provider.InitializeJustDownloadCoreAsync();

        long id = await DownloadUntilAsync(provider, server, "reject.bin", 256 * 1024);
        (await provider.GetRequiredService<ISegmentRepository>().GetByDownloadAsync(id))
            .Should().NotBeEmpty("a paused download keeps its checkpoint");

        // The server now advertises ranges to the probe but ignores real multi-byte ranges.
        server.IgnoreMultiByteRanges = true;

        var manager = provider.GetRequiredService<IDownloadManager>();
        Func<Task> resume = async () => await manager.StartAsync(id);
        await resume.Should().ThrowAsync<ResumeNotSupportedException>();

        Download rec = (await provider.GetRequiredService<IDownloadRepository>().GetAsync(id))!;
        rec.Status.Should().Be(DownloadStatusCodes.Failed);
        rec.Error.Should().Contain("resume", "the failure should explain that resuming is no longer possible");
        (await provider.GetRequiredService<ISegmentRepository>().GetByDownloadAsync(id))
            .Should().BeEmpty("the stale checkpoint is dropped so a restart is clean");
    }

    [Fact]
    public async Task RecoveryScan_IsIdempotent_AndLeavesNonActiveDownloadsUntouched()
    {
        using ServiceProvider provider = BuildProvider();
        await provider.InitializeJustDownloadCoreAsync();

        var downloads = provider.GetRequiredService<IDownloadRepository>();
        long active = await downloads.AddAsync(new Download
        {
            Url = "https://example.com/a.bin",
            Status = DownloadStatusCodes.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        long completed = await downloads.AddAsync(new Download
        {
            Url = "https://example.com/b.bin",
            Status = DownloadStatusCodes.Completed,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var recovery = provider.GetRequiredService<IDownloadRecovery>();
        IReadOnlyList<long> first = await recovery.RecoverInterruptedAsync();
        first.Should().ContainSingle().Which.Should().Be(active);
        (await downloads.GetAsync(active))!.Status.Should().Be(DownloadStatusCodes.Paused);
        (await downloads.GetAsync(completed))!.Status.Should().Be(DownloadStatusCodes.Completed);

        IReadOnlyList<long> second = await recovery.RecoverInterruptedAsync();
        second.Should().BeEmpty("recovery is idempotent — nothing is left active");
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
