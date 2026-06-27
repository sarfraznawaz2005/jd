using FluentAssertions;
using JustDownload.App.ViewModels;
using JustDownload.Core.Lifecycle;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// Unit tests for the live segment visualization (TASK-055): cells fill per connection (AC0), the
/// "Segments: N" count tracks live add/remove (AC1), it renders one stacked strip per stream incl. the
/// two-stream muxed case (AC2), and its repaint rate is capped at ≤ 4 Hz (AC3).
/// </summary>
public sealed class SegmentVisualizationViewModelTests
{
    private static ConnectionStat Stat(int id, double fraction, bool active = true)
    {
        long total = 1000;
        return new ConnectionStat
        {
            ConnectionId = id,
            SegmentIndex = id,
            Start = id * total,
            End = (id * total) + total - 1,
            DownloadedBytes = (long)(fraction * total),
            TotalBytes = total,
            BytesPerSecond = active ? 5000 : 0,
            IsActive = active,
        };
    }

    private static SegmentVisualizationViewModel Build(params StreamSnapshot[] streams) =>
        new(() => streams);

    [Fact]
    public void RepaintRate_IsCappedAtFourHertz()
    {
        SegmentVisualizationViewModel.RepaintHz.Should().BeLessThanOrEqualTo(4);
        SegmentVisualizationViewModel.RepaintInterval.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void Update_FillsOneCellPerConnection_WithItsFraction()
    {
        var vm = Build();
        vm.Update([new StreamSnapshot("File", new[] { Stat(0, 0.62), Stat(1, 0.30) })]);

        vm.Streams.Should().ContainSingle();
        StreamStripViewModel strip = vm.Streams[0];
        strip.SegmentCount.Should().Be(2);
        strip.Cells.Should().HaveCount(2);
        strip.Cells[0].FillPercent.Should().BeApproximately(62, 0.5);
        strip.Cells[1].FillPercent.Should().BeApproximately(30, 0.5);
    }

    [Fact]
    public void Update_TracksSegmentCount_OnLiveAddAndRemove()
    {
        var vm = Build();
        vm.Update([new StreamSnapshot("File", new[] { Stat(0, 0.1), Stat(1, 0.1) })]);
        vm.Streams[0].SegmentCount.Should().Be(2);

        // A work-steal adds a third connection.
        vm.Update([new StreamSnapshot("File", new[] { Stat(0, 0.4), Stat(1, 0.3), Stat(2, 0.05) })]);
        vm.Streams[0].SegmentCount.Should().Be(3);
        vm.Streams[0].Cells.Should().HaveCount(3);

        // Two finish and drop out.
        vm.Update([new StreamSnapshot("File", new[] { Stat(2, 0.9) })]);
        vm.Streams[0].SegmentCount.Should().Be(1);
        vm.Streams[0].Cells.Should().ContainSingle().Which.ConnectionId.Should().Be(2);
    }

    [Fact]
    public void Update_CellsAreUpdatedInPlace_NotRecreated()
    {
        var vm = Build();
        vm.Update([new StreamSnapshot("File", new[] { Stat(0, 0.2) })]);
        SegmentCellViewModel cell = vm.Streams[0].Cells[0];

        vm.Update([new StreamSnapshot("File", new[] { Stat(0, 0.8) })]);

        vm.Streams[0].Cells[0].Should().BeSameAs(cell, "cells update in place so the strip animates");
        cell.FillPercent.Should().BeApproximately(80, 0.5);
    }

    [Fact]
    public void Update_RendersTwoStackedStrips_ForMuxedVideoAndAudio()
    {
        var vm = Build();
        vm.Update(
        [
            new StreamSnapshot("Video", new[] { Stat(0, 0.5), Stat(1, 0.4) }),
            new StreamSnapshot("Audio", new[] { Stat(0, 0.7), Stat(1, 0.6) }),
        ]);

        vm.Streams.Should().HaveCount(2);
        vm.Streams[0].Label.Should().Be("Video");
        vm.Streams[1].Label.Should().Be("Audio");
        vm.Streams[0].SegmentCount.Should().Be(2);
        vm.Streams[1].SegmentCount.Should().Be(2);
    }

    [Fact]
    public void Refresh_PullsFromProvider()
    {
        IReadOnlyList<StreamSnapshot> current = [new StreamSnapshot("File", new[] { Stat(0, 0.25) })];
        var vm = new SegmentVisualizationViewModel(() => current);

        vm.Refresh();
        vm.HasStreams.Should().BeTrue();
        vm.Streams[0].Cells[0].FillPercent.Should().BeApproximately(25, 0.5);

        current = [];
        vm.Refresh();
        vm.HasStreams.Should().BeFalse();
    }
}
