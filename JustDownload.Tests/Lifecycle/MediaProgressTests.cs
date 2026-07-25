using FluentAssertions;
using JustDownload.Core;
using JustDownload.Core.Data;
using JustDownload.Core.Data.Migrations;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Downloading;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Media;
using JustDownload.Core.Media.Extraction;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>
/// How the lifecycle reports a media download's progress (TASK-154). Media sources never advertise a total,
/// so these snapshots are the only signal an unknown-size download has — and they must not outlive the
/// transfer: a report delivered after the run finished used to republish the download as
/// <see cref="DownloadStatus.Active"/>, leaving a completed file stuck on "Downloading" with Pause/Cancel
/// still offered and no completion state (user-reported).
/// </summary>
public sealed class MediaProgressTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ServiceProvider _provider;
    private readonly StubMediaCoordinator _coordinator = new();

    public MediaProgressTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jd-mediaprog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var pathProvider = Substitute.For<IDatabasePathProvider>();
        pathProvider.DatabaseDirectory.Returns(_tempDir);
        pathProvider.DatabasePath.Returns(Path.Combine(_tempDir, "test.db"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(pathProvider);
        services.AddSingleton(new SegmentationOptions());
        services.AddJustDownloadData();
        services.AddJustDownloadTransport();
        services.AddJustDownloadDownloading();
        services.AddJustDownloadLifecycle();

        // Replaces the real coordinator so the test drives the progress reports directly, with no network,
        // ffmpeg, or spawned processes involved.
        services.AddSingleton<IMediaDownloadCoordinator>(_coordinator);
        _provider = services.BuildServiceProvider();
        _provider.GetRequiredService<IMigrationRunner>().Migrate();
    }

    private IDownloadManager Manager => _provider.GetRequiredService<IDownloadManager>();

    private async Task<long> EnqueueMediaAsync() => await Manager.EnqueueAsync(new EnqueueDownloadRequest
    {
        Url = new Uri("https://media.example/video-stream"),
        FileName = "clip.mp4",
        DestinationDirectory = _tempDir,
        MediaKind = MediaKind.SeparateStreams,
    });

    [Fact]
    public async Task MediaProgress_ReportsMeasuredSpeed_WithNoTotalOrFraction()
    {
        long id = await EnqueueMediaAsync();
        _coordinator.DuringDownload = report =>
        {
            report.Report(new MediaDownloadProgress(0, 40_000_000));
            report.Report(new MediaDownloadProgress(0, 112_114_345));
        };

        await Manager.StartAsync(id);

        // The terminal snapshot is what remains; the in-flight ones are asserted through the event stream.
        DownloadProgress? final = Manager.GetProgress(id);
        final.Should().NotBeNull();
        final!.Status.Should().Be(DownloadStatus.Completed);
        final.TotalBytes.Should().Be(112_114_345, "the size is only knowable once the transfer ends");
    }

    [Fact]
    public async Task MediaProgress_WhileRunning_CarriesBytesAndSpeedButNoFraction()
    {
        long id = await EnqueueMediaAsync();
        var seen = new List<DownloadProgress>();
        Manager.ProgressChanged += (_, e) => seen.Add(e.Progress);
        _coordinator.DuringDownload = report => report.Report(new MediaDownloadProgress(0, 40_000_000));

        await Manager.StartAsync(id);

        DownloadProgress running = seen.Should().Contain(p => p.Status == DownloadStatus.Active).Which;
        running.DownloadedBytes.Should().Be(40_000_000);
        running.TotalBytes.Should().BeNull();
        running.Fraction.Should().BeNull("a separate-stream download has no measurable percentage");
    }

    [Fact]
    public async Task CombiningReport_SurfacesAsAProcessingPhaseWithNoSpeed()
    {
        long id = await EnqueueMediaAsync();
        var seen = new List<DownloadProgress>();
        Manager.ProgressChanged += (_, e) => seen.Add(e.Progress);
        _coordinator.DuringDownload = report =>
        {
            report.Report(new MediaDownloadProgress(0, 112_114_345));
            report.Report(new MediaDownloadProgress(0, 112_114_345, MediaDownloadPhase.Combining));
        };

        await Manager.StartAsync(id);

        DownloadProgress combining = seen.Should().ContainSingle(p => p.Phase == DownloadPhase.Processing).Which;
        combining.Status.Should().Be(DownloadStatus.Active, "the download is still working, just not fetching");
        combining.DownloadedBytes.Should().Be(112_114_345, "the byte total holds while the streams are joined");
        combining.BytesPerSecond.Should().Be(0);
    }

    [Fact]
    public async Task CombiningReport_IsNeverDroppedByTheProgressRateLimiter()
    {
        // Ordinary byte ticks are coalesced to ~15Hz; the single phase change must not be coalesced away, or
        // the UI would sit on "Downloading" for the whole merge.
        long id = await EnqueueMediaAsync();
        var seen = new List<DownloadProgress>();
        Manager.ProgressChanged += (_, e) => seen.Add(e.Progress);
        _coordinator.DuringDownload = report =>
        {
            for (int i = 1; i <= 50; i++)
            {
                report.Report(new MediaDownloadProgress(0, i * 1_000_000L));
            }

            report.Report(new MediaDownloadProgress(0, 50_000_000, MediaDownloadPhase.Combining));
        };

        await Manager.StartAsync(id);

        seen.Should().Contain(p => p.Phase == DownloadPhase.Processing);
    }

    [Fact]
    public async Task Forget_DropsTheDownloadsInMemoryState()
    {
        long id = await EnqueueMediaAsync();
        _coordinator.DuringDownload = report => report.Report(new MediaDownloadProgress(0, 10_000));
        await Manager.StartAsync(id);
        Manager.GetProgress(id).Should().NotBeNull();

        Manager.Forget(id);

        Manager.GetProgress(id).Should().BeNull();
        Manager.GetConnections(id).Should().BeEmpty();
    }

    [Fact]
    public async Task LateProgressReport_AfterCompletion_CannotResurrectTheDownload()
    {
        long id = await EnqueueMediaAsync();
        var seen = new List<DownloadProgress>();
        _coordinator.DuringDownload = report => report.Report(new MediaDownloadProgress(0, 112_114_345));

        await Manager.StartAsync(id);
        Manager.ProgressChanged += (_, e) => seen.Add(e.Progress);

        // Exactly the report that raced past the terminal snapshot in the field: the coordinator's own
        // Progress<T> hand-off delivered it after the transfer had already finished.
        _coordinator.CapturedProgress!.Report(new MediaDownloadProgress(0, 112_114_345));

        Manager.GetProgress(id)!.Status.Should().Be(DownloadStatus.Completed);
        seen.Should().BeEmpty("a finished run publishes nothing further");

        Download? record = await _provider.GetRequiredService<IDownloadRepository>().GetAsync(id);
        record!.Status.Should().Be(DownloadStatusCodes.Completed);
    }

    [Fact]
    public async Task RestartingAfterAFinishedRun_ReportsProgressAgain()
    {
        long id = await EnqueueMediaAsync();
        _coordinator.DuringDownload = report => report.Report(new MediaDownloadProgress(0, 10_000));
        await Manager.StartAsync(id);
        _coordinator.CapturedProgress!.Report(new MediaDownloadProgress(0, 10_000)); // fenced off

        // The gate that silences a finished run must reopen for the next one, or a re-download would show
        // no progress at all.
        await _provider.GetRequiredService<IDownloadRepository>().UpdateAsync(
            (await _provider.GetRequiredService<IDownloadRepository>().GetAsync(id))!
                with { Status = DownloadStatusCodes.Paused, CompletedAt = null });

        var seen = new List<DownloadProgress>();
        Manager.ProgressChanged += (_, e) => seen.Add(e.Progress);
        await Manager.StartAsync(id);

        seen.Should().Contain(p => p.Status == DownloadStatus.Active && p.DownloadedBytes == 10_000);
    }

    public void Dispose()
    {
        _provider.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Stands in for the real coordinator: it runs no transfer, but hands the test the exact
    /// <see cref="IProgress{T}"/> the manager supplied so reports can be driven — including after the
    /// download has finished, which is the race this suite pins down.
    /// </summary>
    private sealed class StubMediaCoordinator : IMediaDownloadCoordinator
    {
        public IProgress<MediaDownloadProgress>? CapturedProgress { get; private set; }

        public Action<IProgress<MediaDownloadProgress>>? DuringDownload { get; set; }

        public Task<MediaDownloadOutcome> DownloadAsync(
            MediaDownloadRequest request,
            IProgress<MediaDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CapturedProgress = progress;
            if (progress is not null)
            {
                DuringDownload?.Invoke(progress);
            }

            File.WriteAllBytes(request.OutputPath, new byte[16]);
            return Task.FromResult(new MediaDownloadOutcome(112_114_345));
        }
    }
}
