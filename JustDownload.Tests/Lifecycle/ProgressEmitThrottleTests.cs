using FluentAssertions;
using JustDownload.Core.Lifecycle;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>
/// Unit tests for the progress-notification coalescer (TASK-104): per-download reports are bounded to one
/// emit per interval, the first report always passes, the window is per-download, and a forgotten download
/// starts fresh. Deterministic — driven by explicit timestamps, no wall-clock waits.
/// </summary>
public sealed class ProgressEmitThrottleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(66);

    [Fact]
    public void FirstReport_AlwaysEmits()
    {
        var throttle = new ProgressEmitThrottle(Interval);

        throttle.ShouldEmit(1, T0).Should().BeTrue("the first report for a download is never coalesced");
    }

    [Fact]
    public void InWindowReports_AreCoalesced_AndOnePassesPerInterval()
    {
        var throttle = new ProgressEmitThrottle(Interval);

        throttle.ShouldEmit(1, T0).Should().BeTrue();

        // A burst of reports inside the window are all suppressed.
        throttle.ShouldEmit(1, T0 + TimeSpan.FromMilliseconds(10)).Should().BeFalse();
        throttle.ShouldEmit(1, T0 + TimeSpan.FromMilliseconds(40)).Should().BeFalse();
        throttle.ShouldEmit(1, T0 + TimeSpan.FromMilliseconds(65)).Should().BeFalse();

        // Once the interval elapses, exactly one more passes and re-arms the window.
        throttle.ShouldEmit(1, T0 + Interval).Should().BeTrue();
        throttle.ShouldEmit(1, T0 + Interval + TimeSpan.FromMilliseconds(10)).Should().BeFalse();
    }

    [Fact]
    public void HighFrequencyBurst_IsBoundedToRoughlyTheExpectedRate()
    {
        var throttle = new ProgressEmitThrottle(Interval);

        // 1000 reports over one second (≈1kHz of chunks) must collapse to ~1s/66ms ≈ 15 emits, not 1000.
        int emits = 0;
        for (int i = 0; i < 1000; i++)
        {
            if (throttle.ShouldEmit(1, T0 + TimeSpan.FromMilliseconds(i)))
            {
                emits++;
            }
        }

        emits.Should().BeInRange(14, 16);
    }

    [Fact]
    public void Windows_AreIndependentPerDownload()
    {
        var throttle = new ProgressEmitThrottle(Interval);

        throttle.ShouldEmit(1, T0).Should().BeTrue();
        throttle.ShouldEmit(2, T0).Should().BeTrue("a different download has its own window");
        throttle.ShouldEmit(1, T0 + TimeSpan.FromMilliseconds(10)).Should().BeFalse();
    }

    [Fact]
    public void Forget_ResetsTheWindow()
    {
        var throttle = new ProgressEmitThrottle(Interval);

        throttle.ShouldEmit(1, T0).Should().BeTrue();
        throttle.ShouldEmit(1, T0 + TimeSpan.FromMilliseconds(10)).Should().BeFalse();

        throttle.Forget(1);

        throttle.ShouldEmit(1, T0 + TimeSpan.FromMilliseconds(10))
            .Should().BeTrue("a forgotten download's next report is treated as its first again");
    }
}
