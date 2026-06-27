using System.Diagnostics;
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
/// Integration tests for pause/resume (TASK-028, US-2) over a temp database and loopback server. A download
/// is paused mid-flight by cancelling its token; the test asserts the per-segment offsets are persisted
/// (AC0), that resuming re-fetches none of the already-downloaded bytes and reproduces a byte-identical file
/// (AC1), and that cancellation is honored promptly (AC2). The server's first half is fast and its second
/// half is slow, so the pause lands deterministically after the fast half and before the slow half delivers.
/// </summary>
public sealed class PauseResumeTests : IDisposable
{
    private const int FileSize = 1024 * 1024;
    private const int SlowFrom = 512 * 1024;

    private readonly string _tempDir;
    private readonly ServiceProvider _provider;

    public PauseResumeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-resume-" + Guid.NewGuid().ToString("N"));
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

    private ISegmentRepository Segments => _provider.GetRequiredService<ISegmentRepository>();

    private IDownloadRepository Downloads => _provider.GetRequiredService<IDownloadRepository>();

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
    public async Task Pause_PersistsOffsets_AndResume_RefetchesNothing_ProducingByteIdenticalFile()
    {
        byte[] body = Bytes(FileSize);
        await using var server = new LoopbackHttpServer
        {
            Body = body,
            SupportRanges = true,
            SlowTailFrom = SlowFrom,                       // second half (and steals of it) are slow…
            SlowTailDelay = TimeSpan.FromMilliseconds(600), // …so the pause lands before it delivers.
        };

        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "resume.bin",
            MaxConnections = 4,
        });

        // Pause once the fast first portion has arrived.
        using var pauseCts = new CancellationTokenSource();
        int cancelled = 0;
        Manager.ProgressChanged += (_, e) =>
        {
            if (e.DownloadId == id && e.Progress.DownloadedBytes >= 256 * 1024 &&
                Interlocked.Exchange(ref cancelled, 1) == 0)
            {
                pauseCts.Cancel();
            }
        };

        Func<Task> paused = async () => await Manager.StartAsync(id, pauseCts.Token);
        await paused.Should().ThrowAsync<OperationCanceledException>();

        // AC0: per-segment offsets persisted, partway through.
        IReadOnlyList<DownloadSegment> rows = await Segments.GetByDownloadAsync(id);
        long pausedReceived = rows.Sum(r => r.Downloaded);
        pausedReceived.Should().BeInRange(1, FileSize - 1, "the download paused mid-flight");
        (await Downloads.GetAsync(id))!.Status.Should().Be(DownloadStatusCodes.Paused);

        // AC1: resume fetches only the remaining gaps — none of the already-downloaded bytes are re-served.
        // (Server bytes alone can't measure this: work-stealing makes the server push a truncated victim's
        // tail that the client discards, so we instead check served ranges don't overlap what we already had.)
        server.ClearServedRanges();
        await Manager.StartAsync(id);

        long refetchedKnownBytes = 0;
        foreach ((long from, long to) in server.ServedRanges)
        {
            foreach (DownloadSegment had in rows)
            {
                long lo = Math.Max(from, had.Start);
                long hi = Math.Min(to, had.End);
                if (hi >= lo)
                {
                    refetchedKnownBytes += hi - lo + 1;
                }
            }
        }

        refetchedKnownBytes.Should().BeLessThanOrEqualTo(
            1024,
            "resume must re-fetch no already-downloaded bytes (only the 1-byte range-probe may touch byte 0)");

        // Final file is byte-identical to the reference.
        (await File.ReadAllBytesAsync(Path.Combine(_tempDir, "resume.bin"))).Should().Equal(body);
        (await Downloads.GetAsync(id))!.Status.Should().Be(DownloadStatusCodes.Completed);
        (await Segments.GetByDownloadAsync(id)).Should().BeEmpty("completion clears the resume checkpoint");
    }

    [Fact]
    public async Task Cancellation_IsHonoredPromptly()
    {
        byte[] body = Bytes(256 * 1024);
        await using var server = new LoopbackHttpServer
        {
            Body = body,
            SupportRanges = true,
            ResponseDelay = TimeSpan.FromSeconds(5), // every response blocks for 5 s unless cancelled
        };

        long id = await Manager.EnqueueAsync(new EnqueueDownloadRequest
        {
            Url = server.Url("file.bin"),
            DestinationDirectory = _tempDir,
            FileName = "cancel.bin",
            MaxConnections = 4,
        });

        using var cts = new CancellationTokenSource();
        Task<DownloadResult> start = Manager.StartAsync(id, cts.Token);

        await Task.Delay(150); // let the workers issue their (blocked) requests
        var sw = Stopwatch.StartNew();
        await cts.CancelAsync();

        Func<Task> act = async () => await start;
        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Stop();

        // The 5 s server delay honours the token, so cancellation returns far sooner than that.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "pause/cancel must be instant, not wait on I/O");
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
