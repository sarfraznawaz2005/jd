using FluentAssertions;
using JustDownload.Core.Lifecycle;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>
/// Unit tests for the deterministic sliding-window speed estimator (TASK-031 AC1). Timestamps are supplied
/// explicitly, so the estimate is fully reproducible with no real-clock dependency.
/// </summary>
public sealed class SpeedEstimatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sample_SteadyRate_ReportsThatRate()
    {
        var estimator = new SpeedEstimator(TimeSpan.FromSeconds(10));

        estimator.Sample(T0, 0).Should().Be(0); // first sample has no interval
        estimator.Sample(T0 + TimeSpan.FromSeconds(1), 1_000_000);
        double rate = estimator.Sample(T0 + TimeSpan.FromSeconds(2), 2_000_000);

        rate.Should().BeApproximately(1_000_000, 1); // 2 MB over 2 s = 1 MB/s
    }

    [Fact]
    public void Sample_IgnoresOutOfOrderLowerCounts()
    {
        // A stale, lower cumulative report (arriving late from another worker thread) must have no effect:
        // an estimator that receives it should end up identical to one that never did.
        var withStale = new SpeedEstimator(TimeSpan.FromSeconds(10));
        var control = new SpeedEstimator(TimeSpan.FromSeconds(10));
        foreach (SpeedEstimator e in new[] { withStale, control })
        {
            e.Sample(T0, 0);
            e.Sample(T0 + TimeSpan.FromSeconds(1), 1_000_000);
        }

        withStale.Sample(T0 + TimeSpan.FromSeconds(2), 500_000); // stale — ignored

        // Feed both the same later valid reading; the rates must match (the stale sample changed nothing).
        double rateWithStale = withStale.Sample(T0 + TimeSpan.FromSeconds(3), 3_000_000);
        double rateControl = control.Sample(T0 + TimeSpan.FromSeconds(3), 3_000_000);

        rateWithStale.Should().Be(rateControl);
        rateWithStale.Should().BeApproximately(1_000_000, 1); // 3 MB over 3 s
    }

    [Fact]
    public void CurrentRate_DropsToZero_WhenWindowPassesWithoutNewData()
    {
        var estimator = new SpeedEstimator(TimeSpan.FromSeconds(2));
        estimator.Sample(T0, 0);
        estimator.Sample(T0 + TimeSpan.FromSeconds(1), 1_000_000);

        // Long after the last sample, with the window kept to two readings, the rate over the elapsed
        // span collapses toward zero rather than reporting a stale high speed.
        double later = estimator.CurrentRate(T0 + TimeSpan.FromSeconds(101));
        later.Should().BeLessThan(11_000); // 1 MB / ~100 s ≈ 10 KB/s
    }

    [Fact]
    public void Constructor_RejectsNonPositiveWindow()
    {
        Action act = () => _ = new SpeedEstimator(TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
