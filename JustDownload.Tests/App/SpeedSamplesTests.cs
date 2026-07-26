using FluentAssertions;
using JustDownload.App.ViewModels;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>The speed-sparkline series (TASK-137): rolling window + peak-normalized bar heights.</summary>
public sealed class SpeedSamplesTests
{
    [Fact]
    public void Add_BuildsPeakNormalizedBars()
    {
        var series = new SpeedSamples(capacity: 5);

        series.Add(100);
        series.Add(50);

        series.Count.Should().Be(2);
        series.Peak.Should().Be(100);
        series.Bars.Should().HaveCount(2);
        series.Bars[0].Height.Should().Be(series.BarHeight, "the peak sample is full height");
        series.Bars[1].Height.Should().Be(series.BarHeight / 2, "half the peak is half height");
    }

    /// <summary>
    /// The bar height is per-series so a wide host (the detached progress window) can draw a taller chart;
    /// the peak still reaches full scale, whatever that scale is.
    /// </summary>
    [Fact]
    public void BarHeight_ScalesTheSeries_ToTheRequestedFullScale()
    {
        var series = new SpeedSamples(capacity: 5, barHeight: 40);

        series.Add(100);
        series.Add(25);

        series.BarHeight.Should().Be(40);
        series.Bars[0].Height.Should().Be(40);
        series.Bars[1].Height.Should().Be(10);
    }

    [Fact]
    public void BarHeight_MustBePositive()
    {
        Action zero = () => _ = new SpeedSamples(barHeight: 0);

        zero.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Add_BeyondCapacity_DropsOldest()
    {
        var series = new SpeedSamples(capacity: 3);

        series.Add(1);
        series.Add(2);
        series.Add(3);
        series.Add(4); // drops the '1'

        series.Count.Should().Be(3);
        series.Peak.Should().Be(4, "the window is now [2,3,4]");
        series.Bars.Should().HaveCount(3);
    }

    [Fact]
    public void Empty_AndAllZero_RenderFlat()
    {
        var series = new SpeedSamples();
        series.Bars.Should().BeEmpty();

        series.Add(0);
        series.Add(0);

        series.Bars.Should().HaveCount(2);
        series.Bars.Should().OnlyContain(b => b.Height == 0, "an all-zero window is flat, not divide-by-zero");
    }

    [Fact]
    public void Add_ClampsNegativeToZero()
    {
        var series = new SpeedSamples();

        series.Add(-5);

        series.Peak.Should().Be(0);
    }

    [Fact]
    public void Clear_ResetsTheWindow()
    {
        var series = new SpeedSamples();
        series.Add(10);
        series.Add(20);

        series.Clear();

        series.Count.Should().Be(0);
        series.Peak.Should().Be(0);
        series.Bars.Should().BeEmpty();
    }
}
