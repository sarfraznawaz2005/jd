using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using JustDownload.App.ViewModels;
using JustDownload.Core.Lifecycle;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// Headless tests for the live status summary (TASK-049 AC2): it folds the manager's status/progress events
/// into the active-count, total-speed and connection figures, published on the UI thread.
/// </summary>
public sealed class StatusSummaryViewModelTests
{
    [AvaloniaFact]
    public void ReflectsActiveDownloadsAndTheirSpeedAndConnections()
    {
        var manager = Substitute.For<IDownloadManager>();
        var vm = new StatusSummaryViewModel(manager);

        manager.StatusChanged += Raise.Event<EventHandler<DownloadStatusChangedEventArgs>>(
            manager, new DownloadStatusChangedEventArgs(1, null, DownloadStatus.Active));
        manager.ProgressChanged += Raise.Event<EventHandler<DownloadProgressChangedEventArgs>>(
            manager,
            new DownloadProgressChangedEventArgs(
                1, DownloadProgress.Create(DownloadStatus.Active, 50, 100, 442_000, resumable: true, connections: 8)));
        Dispatcher.UIThread.RunJobs();

        vm.ActiveCount.Should().Be(1);
        vm.Connections.Should().Be(8);
        vm.TotalSpeedDisplay.Should().Contain("KB/s");
    }

    [AvaloniaFact]
    public void SampleNow_RecordsCombinedSpeed_IntoTheSparklineHistory()
    {
        var manager = Substitute.For<IDownloadManager>();
        var vm = new StatusSummaryViewModel(manager);
        manager.StatusChanged += Raise.Event<EventHandler<DownloadStatusChangedEventArgs>>(
            manager, new DownloadStatusChangedEventArgs(1, null, DownloadStatus.Active));
        manager.ProgressChanged += Raise.Event<EventHandler<DownloadProgressChangedEventArgs>>(
            manager,
            new DownloadProgressChangedEventArgs(
                1, DownloadProgress.Create(DownloadStatus.Active, 50, 100, 442_000, resumable: true, connections: 8)));
        Dispatcher.UIThread.RunJobs();

        vm.SampleNow();
        vm.SampleNow();

        vm.SpeedHistory.Count.Should().Be(2);
        vm.SpeedHistory.Peak.Should().Be(442_000, "the sampled combined speed is recorded for the sparkline");
    }

    [AvaloniaFact]
    public void DropsDownloadsThatLeaveTheActiveState()
    {
        var manager = Substitute.For<IDownloadManager>();
        var vm = new StatusSummaryViewModel(manager);

        manager.StatusChanged += Raise.Event<EventHandler<DownloadStatusChangedEventArgs>>(
            manager, new DownloadStatusChangedEventArgs(1, null, DownloadStatus.Active));
        manager.ProgressChanged += Raise.Event<EventHandler<DownloadProgressChangedEventArgs>>(
            manager,
            new DownloadProgressChangedEventArgs(
                1, DownloadProgress.Create(DownloadStatus.Active, 50, 100, 1000, resumable: true, connections: 4)));
        Dispatcher.UIThread.RunJobs();
        vm.ActiveCount.Should().Be(1);

        // The download completes — it should leave the active totals.
        manager.StatusChanged += Raise.Event<EventHandler<DownloadStatusChangedEventArgs>>(
            manager, new DownloadStatusChangedEventArgs(1, DownloadStatus.Active, DownloadStatus.Completed));
        Dispatcher.UIThread.RunJobs();

        vm.ActiveCount.Should().Be(0);
        vm.Connections.Should().Be(0);
        vm.TotalSpeedDisplay.Should().Be("—");
    }
}
