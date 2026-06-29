using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.Core.Categorization;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Lifecycle;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// Headless tests for the per-download detail view-model (TASK-054): it tracks the selection, folds live
/// stats and per-connection rows from the manager, exposes the Options fields, and routes the per-item
/// Pause/Resume/Cancel and Detach actions.
/// </summary>
public sealed class DownloadDetailViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);

    private static DownloadRowViewModel Row(long id = 1, string status = DownloadStatusCodes.Active) =>
        new(
            new Download
            {
                Id = id,
                Url = "https://host.example/big.iso",
                Filename = "big.iso",
                Directory = @"C:\Downloads\Compressed",
                TotalBytes = 4_000_000,
                Status = status,
                CreatedAt = Now,
            },
            Now,
            FileCategory.Compressed);

    private static ConnectionStat Stat(int id, long start, long end, long downloaded, double bps, bool active = true) =>
        new()
        {
            ConnectionId = id,
            SegmentIndex = id,
            Start = start,
            End = end,
            DownloadedBytes = downloaded,
            TotalBytes = end - start + 1,
            BytesPerSecond = bps,
            IsActive = active,
        };

    [AvaloniaFact]
    public void Select_PopulatesHeaderAndOptions()
    {
        var manager = Substitute.For<IDownloadManager>();
        manager.GetConnections(Arg.Any<long>()).Returns([]);
        var vm = new DownloadDetailViewModel(manager, Substitute.For<IDownloadActions>());

        vm.HasSelection.Should().BeFalse();

        vm.Select(Row());

        vm.HasSelection.Should().BeTrue();
        vm.UrlDisplay.Should().Be("https://host.example/big.iso");
        vm.SaveToDisplay.Should().Be(@"C:\Downloads\Compressed");
        vm.CategoryDisplay.Should().Be("Compressed");
        vm.TotalSizeDisplay.Should().Be("3.8 MB");
    }

    [AvaloniaFact]
    public void ProgressTick_RefreshesStatsAndConnections()
    {
        var manager = Substitute.For<IDownloadManager>();
        manager.GetProgress(1).Returns(DownloadProgress.Create(
            DownloadStatus.Active, 1_000_000, 4_000_000, 442_000, resumable: true, connections: 3));
        manager.GetConnections(1).Returns(new[]
        {
            Stat(0, 0, 1_999_999, 800_000, 200_000),
            Stat(1, 2_000_000, 3_999_999, 200_000, 120_000),
        });
        var vm = new DownloadDetailViewModel(manager, Substitute.For<IDownloadActions>());
        vm.Select(Row());

        manager.ProgressChanged += Raise.Event<EventHandler<DownloadProgressChangedEventArgs>>(
            manager,
            new DownloadProgressChangedEventArgs(
                1, DownloadProgress.Create(DownloadStatus.Active, 1_000_000, 4_000_000, 442_000, resumable: true, connections: 3)));
        Dispatcher.UIThread.RunJobs();

        vm.DownloadedDisplay.Should().Contain("KB"); // 1,000,000 bytes ≈ 976.6 KB
        vm.SegmentsDisplay.Should().Be("3");
        vm.Connections.Should().HaveCount(2);
        vm.Connections[0].DisplayNumber.Should().Be(1);
        vm.Connections[0].SpeedDisplay.Should().Contain("KB/s");
    }

    [AvaloniaFact]
    public void SampleNow_RecordsSelectedDownloadSpeed_AndResetsOnReselect()
    {
        var manager = Substitute.For<IDownloadManager>();
        manager.GetConnections(Arg.Any<long>()).Returns([]);
        var vm = new DownloadDetailViewModel(manager, Substitute.For<IDownloadActions>());
        vm.Select(Row());

        manager.ProgressChanged += Raise.Event<EventHandler<DownloadProgressChangedEventArgs>>(
            manager,
            new DownloadProgressChangedEventArgs(
                1, DownloadProgress.Create(DownloadStatus.Active, 50, 100, 442_000, resumable: true, connections: 3)));
        Dispatcher.UIThread.RunJobs();

        vm.SampleNow();
        vm.SpeedHistory.Count.Should().Be(1);
        vm.SpeedHistory.Peak.Should().Be(442_000);

        vm.Select(Row(id: 2)); // a different download starts with a fresh graph
        vm.SpeedHistory.Count.Should().Be(0);
    }

    [AvaloniaFact]
    public void Connections_UpdateInPlace_AndDropWhenGone()
    {
        var manager = Substitute.For<IDownloadManager>();
        manager.GetProgress(1).Returns(DownloadProgress.Create(DownloadStatus.Active, 1, 4_000_000, 1, resumable: true));
        manager.GetConnections(1).Returns(new[] { Stat(0, 0, 999, 500, 100), Stat(1, 1000, 1999, 400, 90) });
        var vm = new DownloadDetailViewModel(manager, Substitute.For<IDownloadActions>());
        vm.Select(Row());
        RaiseProgress(manager, 1);
        Dispatcher.UIThread.RunJobs();
        vm.Connections.Should().HaveCount(2);
        ConnectionRowViewModel first = vm.Connections[0];

        // One connection finished — only connection 0 remains; the row instance is reused (in-place update).
        manager.GetConnections(1).Returns(new[] { Stat(0, 0, 999, 900, 110) });
        RaiseProgress(manager, 1);
        Dispatcher.UIThread.RunJobs();

        vm.Connections.Should().ContainSingle();
        vm.Connections[0].Should().BeSameAs(first, "existing rows are updated in place, not recreated");
    }

    [AvaloniaFact]
    public void PerItemActions_RouteToTheActionSurface_WithContextualEnablement()
    {
        var manager = Substitute.For<IDownloadManager>();
        manager.GetConnections(Arg.Any<long>()).Returns([]);
        var actions = Substitute.For<IDownloadActions>();
        var vm = new DownloadDetailViewModel(manager, actions);

        vm.ResumeCommand.CanExecute(null).Should().BeFalse("nothing selected");

        vm.Select(Row(status: DownloadStatusCodes.Active));
        vm.PauseCommand.CanExecute(null).Should().BeTrue();
        vm.ResumeCommand.CanExecute(null).Should().BeFalse();
        vm.CancelCommand.CanExecute(null).Should().BeTrue();

        vm.PauseCommand.Execute(null);
        actions.Received(1).Pause(1);
        vm.CancelCommand.Execute(null);
        actions.Received(2).Pause(1); // cancel also stops the transfer
    }

    [AvaloniaFact]
    public void Detach_RaisesDetachRequestedForSelection()
    {
        var manager = Substitute.For<IDownloadManager>();
        manager.GetConnections(Arg.Any<long>()).Returns([]);
        var vm = new DownloadDetailViewModel(manager, Substitute.For<IDownloadActions>());
        DownloadRowViewModel row = Row();
        vm.Select(row);

        DownloadRowViewModel? detached = null;
        vm.DetachRequested += (_, r) => detached = r;

        vm.DetachCommand.CanExecute(null).Should().BeTrue();
        vm.DetachCommand.Execute(null);

        detached.Should().BeSameAs(row);
    }

    [AvaloniaFact]
    public void SegmentVisualization_RunsWhileActive_AndShowsTheStream()
    {
        var manager = Substitute.For<IDownloadManager>();
        manager.GetConnections(1).Returns(new[]
        {
            Stat(0, 0, 999, 600, 100),
            Stat(1, 1000, 1999, 300, 90),
        });
        var vm = new DownloadDetailViewModel(manager, Substitute.For<IDownloadActions>());

        vm.Select(Row(status: DownloadStatusCodes.Active));

        vm.Segments.IsRunning.Should().BeTrue("the repaint loop runs while the download is active");
        vm.Segments.HasStreams.Should().BeTrue();
        vm.Segments.Streams.Should().ContainSingle();
        vm.Segments.Streams[0].Label.Should().Be("File");
        vm.Segments.Streams[0].SegmentCount.Should().Be(2);

        vm.Dispose();
    }

    [AvaloniaFact]
    public void SegmentVisualization_StopsWhenNotActive()
    {
        var manager = Substitute.For<IDownloadManager>();
        manager.GetConnections(Arg.Any<long>()).Returns([]);
        var vm = new DownloadDetailViewModel(manager, Substitute.For<IDownloadActions>());

        vm.Select(Row(status: DownloadStatusCodes.Paused));

        vm.Segments.IsRunning.Should().BeFalse("a paused download has no live segment repaint");
        vm.Segments.HasStreams.Should().BeFalse();
    }

    private static void RaiseProgress(IDownloadManager manager, long id) =>
        manager.ProgressChanged += Raise.Event<EventHandler<DownloadProgressChangedEventArgs>>(
            manager,
            new DownloadProgressChangedEventArgs(
                id, DownloadProgress.Create(DownloadStatus.Active, 1, 4_000_000, 1, resumable: true)));

    private static DownloadRowViewModel CompletedRowOverFile(string dir, string fileName) =>
        new(
            new Download
            {
                Id = 1,
                Url = "https://host.example/big.iso",
                Filename = fileName,
                Directory = dir,
                TotalBytes = 1000,
                Status = DownloadStatusCodes.Completed,
                CreatedAt = Now,
            },
            Now,
            FileCategory.Compressed);

    [AvaloniaFact]
    public async Task VerifyChecksum_MatchingHash_ShowsMatch()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jd-detail-checksum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] body = System.Security.Cryptography.RandomNumberGenerator.GetBytes(1000);
            File.WriteAllBytes(Path.Combine(dir, "big.iso"), body);
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant();

            var manager = Substitute.For<IDownloadManager>();
            manager.GetConnections(Arg.Any<long>()).Returns([]);
            var vm = new DownloadDetailViewModel(manager, Substitute.For<IDownloadActions>());
            vm.Select(CompletedRowOverFile(dir, "big.iso"));

            vm.ExpectedChecksum = sha;
            vm.VerifyChecksumCommand.CanExecute(null).Should().BeTrue();
            await vm.VerifyChecksumCommand.ExecuteAsync(null);

            vm.ChecksumStatus.Should().Be("✓ Matches");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task VerifyChecksum_WrongHash_ShowsMismatch()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jd-detail-checksum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "big.iso"), new byte[] { 1, 2, 3 });

            var manager = Substitute.For<IDownloadManager>();
            manager.GetConnections(Arg.Any<long>()).Returns([]);
            var vm = new DownloadDetailViewModel(manager, Substitute.For<IDownloadActions>());
            vm.Select(CompletedRowOverFile(dir, "big.iso"));

            vm.ExpectedChecksum = new string('a', 64);
            await vm.VerifyChecksumCommand.ExecuteAsync(null);

            vm.ChecksumStatus.Should().Be("✗ Does not match");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public void VerifyChecksum_NotAllowed_WhenNotCompletedOrNoHash()
    {
        var manager = Substitute.For<IDownloadManager>();
        manager.GetConnections(Arg.Any<long>()).Returns([]);
        var vm = new DownloadDetailViewModel(manager, Substitute.For<IDownloadActions>());

        // Active download with a hash → not verifiable (not complete).
        vm.Select(Row(status: DownloadStatusCodes.Active));
        vm.ExpectedChecksum = new string('a', 64);
        vm.VerifyChecksumCommand.CanExecute(null).Should().BeFalse("only a completed download can be verified");

        // Completed download but no hash entered → not verifiable.
        vm.Select(Row(status: DownloadStatusCodes.Completed));
        vm.VerifyChecksumCommand.CanExecute(null).Should().BeFalse("a hash is required");
    }
}
