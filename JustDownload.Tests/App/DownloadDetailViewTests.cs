using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.App.Views;
using JustDownload.Core.Categorization;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Lifecycle;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>Headless test that the inline detail surface mounts with its three tabs (TASK-054 AC0).</summary>
public sealed class DownloadDetailViewTests
{
    private static DownloadDetailViewModel BuildViewModel()
    {
        var manager = Substitute.For<IDownloadManager>();
        manager.GetConnections(Arg.Any<long>()).Returns([]);
        return new DownloadDetailViewModel(manager, Substitute.For<IDownloadActions>());
    }

    [AvaloniaFact]
    public void InlineView_Mounts_WithThreeTabs()
    {
        var view = new DownloadDetailView { DataContext = BuildViewModel() };
        var host = new Window { Content = view };
        host.Show();

        TabControl tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
        string[] headers = tabs.Items.OfType<TabItem>().Select(t => t.Header?.ToString()).ToArray()!;
        headers.Should().Equal("Download", "Options", "Connections");
    }

    /// <summary>
    /// Regression (user-reported): the tab body used to be a DockPanel fill child, so it stretched to the
    /// whole pane while its content stayed top-aligned — leaving a large band of dead space between the last
    /// visible control and the Resume/Pause/Cancel row. The actions row must sit directly under the tab
    /// content (its own 14px margin and nothing more), in every state, without anything needing to scroll.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(DownloadStatusCodes.Active, true)]
    [InlineData(DownloadStatusCodes.Completed, false)]
    public void ActionsRow_SitsDirectlyBelowTabContent_LeavingNoDeadSpace(string status, bool hasConnections)
    {
        var manager = Substitute.For<IDownloadManager>();
        manager.GetConnections(Arg.Any<long>()).Returns(hasConnections ? [Stat()] : []);
        manager.GetProgress(Arg.Any<long>()).Returns(
            DownloadProgress.Create(DownloadStatus.Active, 50, 100, 1000, resumable: true, connections: 1));

        var vm = new DownloadDetailViewModel(manager, Substitute.For<IDownloadActions>());
        vm.Select(Row(status));
        vm.SampleNow(); // the sparkline is at its tallest with samples in it

        var view = new DownloadDetailView { DataContext = vm };
        // Deliberately far taller than the content: the old fill-child layout absorbed all of it as a gap.
        var host = new Window { Content = view, Width = 420, Height = 900 };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        host.Measure(new Size(420, 900));
        host.Arrange(new Rect(0, 0, 420, 900));

        ContentPresenter body = view.GetVisualDescendants().OfType<ContentPresenter>()
            .Single(p => p.Name == "PART_SelectedContentHost");
        Control actions = view.GetVisualDescendants().OfType<Button>()
            .Single(b => (b.Content as string) == "Resume");

        double contentBottom = body.Bounds.Height + body.TranslatePoint(default, view)!.Value.Y;
        double actionsTop = actions.TranslatePoint(default, view)!.Value.Y;

        (actionsTop - contentBottom).Should().BeLessThanOrEqualTo(
            16, "the actions row carries a 14px top margin and nothing else should separate it from the tab body");

        if (body.Child is Control tabContent)
        {
            tabContent.Measure(new Size(body.Bounds.Width, double.PositiveInfinity));
            tabContent.DesiredSize.Height.Should().BeLessThanOrEqualTo(
                body.Bounds.Height + 1, "the tab body sizes to its content, so nothing needs to scroll");
        }
    }

    /// <summary>
    /// The detached progress window (IsWide) is far wider than the docked pane, so it lays the Download tab
    /// out differently (user-requested): the speed chart spans the full width like the segment strip below
    /// it, and the four stats collapse from a 2×2 block onto one row — leading with the two that move every
    /// tick. The docked pane must keep its existing layout.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void WideHost_SpansTheSpeedChart_AndPutsTheStatsOnOneRow(bool isWide)
    {
        var manager = Substitute.For<IDownloadManager>();
        manager.GetConnections(Arg.Any<long>()).Returns([]);
        manager.GetProgress(Arg.Any<long>()).Returns(
            DownloadProgress.Create(DownloadStatus.Active, 50, 100, 1000, resumable: true, connections: 1));

        var vm = new DownloadDetailViewModel(manager, Substitute.For<IDownloadActions>());
        vm.Select(Row(DownloadStatusCodes.Active));
        vm.SampleNow();

        var view = new DownloadDetailView { DataContext = vm, IsWide = isWide };
        var host = new Window { Content = view, Width = 700, Height = 900 };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        host.Measure(new Size(700, 900));
        host.Arrange(new Rect(0, 0, 700, 900));

        Grid Stats(string name) => view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == name);
        Border chart = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SpeedChart");

        Stats("WideStats").IsVisible.Should().Be(isWide);
        Stats("NarrowStats").IsVisible.Should().Be(!isWide);

        if (isWide)
        {
            chart.Bounds.Width.Should().BeGreaterThan(
                400, "a wide host drops the chart's fixed width so it fills the row like the segment strip");
        }
        else
        {
            chart.Bounds.Width.Should().Be(251, "the docked pane keeps its fixed-width chart card");
        }
    }

    /// <summary>
    /// The chart card takes its height from the series' full-scale bar height (plus its 6px padding), so a
    /// host that asks for taller bars gets a proportionally taller card and the peak bar still reaches the
    /// top. A card sized independently would leave the bars hugging the bottom of a half-empty box.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(24)]
    [InlineData(40)]
    public void SpeedChartCard_SizesToTheSeriesBarHeight(double barHeight)
    {
        var manager = Substitute.For<IDownloadManager>();
        manager.GetConnections(Arg.Any<long>()).Returns([]);

        var vm = new DownloadDetailViewModel(
            manager, Substitute.For<IDownloadActions>(), speedHistory: new SpeedSamples(barHeight: barHeight));
        vm.Select(Row(DownloadStatusCodes.Active));
        vm.SampleNow();

        var view = new DownloadDetailView { DataContext = vm, IsWide = true };
        var host = new Window { Content = view, Width = 700, Height = 900 };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        host.Measure(new Size(700, 900));
        host.Arrange(new Rect(0, 0, 700, 900));

        Border chart = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SpeedChart");
        chart.Bounds.Height.Should().Be(barHeight + 12);

        // Slots come from the series' capacity, never the current bar count — one sample must not become one
        // full-width bar that then shrinks on every tick.
        UniformGrid slots = view.GetVisualDescendants().OfType<UniformGrid>()
            .Single(g => g.Name == "SpeedChartSlots");
        slots.Columns.Should().Be(vm.SpeedHistory.Capacity);
        vm.SpeedHistory.Count.Should().Be(1, "only one sample has been taken");
    }

    [AvaloniaFact]
    public void WideStats_ReadBandwidthTimeLeftDownloadedTotalSize_LeftToRight()
    {
        var vm = BuildViewModel();
        vm.Select(Row(DownloadStatusCodes.Active));

        var view = new DownloadDetailView { DataContext = vm, IsWide = true };
        var host = new Window { Content = view, Width = 700, Height = 900 };
        host.Show();
        Dispatcher.UIThread.RunJobs();

        Grid stats = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "WideStats");
        string[] keys = stats.Children.OfType<StackPanel>()
            .OrderBy(Grid.GetColumn)
            .Select(p => ((TextBlock)p.Children[0]).Text!)
            .ToArray();

        keys.Should().Equal("Bandwidth", "Time left", "Downloaded", "Total size");
    }

    private static ConnectionStat Stat() => new()
    {
        ConnectionId = 1,
        SegmentIndex = 0,
        Start = 0,
        End = 999,
        DownloadedBytes = 500,
        TotalBytes = 1000,
        BytesPerSecond = 1000,
        IsActive = true,
    };

    private static DownloadRowViewModel Row(string status) =>
        new(
            new Download
            {
                Id = 1,
                Url = "https://host.example/big.iso",
                Filename = "big.iso",
                Directory = @"C:\Downloads",
                TotalBytes = 4_000_000,
                Status = status,
                CreatedAt = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero),
            },
            new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero),
            FileCategory.Compressed);
}
