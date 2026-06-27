using FluentAssertions;
using JustDownload.Core.Throttling;
using JustDownload.Tests.Fakes;
using Xunit;

namespace JustDownload.Tests.Throttling;

/// <summary>
/// Deterministic unit tests for the token-bucket throttle (TASK-030) using a controllable clock. Cover
/// unlimited pass-through (AC2), waiting/refill maths, capacity capping, and a live rate change (AC1).
/// </summary>
public sealed class TokenBucketTests
{
    [Fact]
    public void Unlimited_GrantsImmediately()
    {
        // AC2: rate 0 = unlimited → never waits, never depletes.
        var bucket = new TokenBucket(new TestClock(), bytesPerSecond: 0);

        bucket.Reserve(1_000_000).Should().Be(TimeSpan.Zero);
        bucket.Reserve(1_000_000).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Reserve_WaitsForDeficit_ThenGrantsAfterRefill()
    {
        var clock = new TestClock();
        var bucket = new TokenBucket(clock, bytesPerSecond: 1000);

        // Starts empty: 500 bytes need 500/1000 = 0.5s, and nothing is consumed yet.
        bucket.Reserve(500).Should().Be(TimeSpan.FromSeconds(0.5));

        clock.Advance(TimeSpan.FromSeconds(0.5)); // refills 500 tokens
        bucket.Reserve(500).Should().Be(TimeSpan.Zero); // now granted (and consumed)

        // Bucket is empty again immediately afterwards.
        bucket.Reserve(500).Should().Be(TimeSpan.FromSeconds(0.5));
    }

    [Fact]
    public void Refill_IsCappedAtOneSecondBurst()
    {
        var clock = new TestClock();
        var bucket = new TokenBucket(clock, bytesPerSecond: 1000);

        clock.Advance(TimeSpan.FromSeconds(100)); // would be 100_000 tokens, but capacity caps it

        // Capacity is max(rate, 64 KiB) = 65536, so a 65536 acquire is granted and a further byte waits.
        bucket.Reserve(65536).Should().Be(TimeSpan.Zero);
        bucket.Reserve(1).Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void SetRate_TakesEffectImmediately()
    {
        // AC1: changing the cap changes the very next reservation, no restart.
        var clock = new TestClock();
        var bucket = new TokenBucket(clock, bytesPerSecond: 1000);

        bucket.Reserve(2000).Should().Be(TimeSpan.FromSeconds(2)); // 2000 @ 1000 B/s

        bucket.BytesPerSecond = 2000; // raise the cap live
        bucket.Reserve(2000).Should().Be(TimeSpan.FromSeconds(1)); // 2000 @ 2000 B/s
    }

    [Fact]
    public void SetRate_ToUnlimited_StopsThrottling()
    {
        // AC2: setting 0 mid-flight removes throttling.
        var bucket = new TokenBucket(new TestClock(), bytesPerSecond: 1000);
        bucket.Reserve(5000).Should().BeGreaterThan(TimeSpan.Zero);

        bucket.BytesPerSecond = 0;
        bucket.Reserve(5000).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void BytesPerSecond_Negative_Throws()
    {
        var bucket = new TokenBucket(new TestClock());
        Action act = () => bucket.BytesPerSecond = -1;
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task AcquireAsync_NonPositive_ReturnsImmediately()
    {
        var bucket = new TokenBucket(new TestClock(), bytesPerSecond: 1);
        await bucket.AcquireAsync(0);
        await bucket.AcquireAsync(-5);
    }

    [Fact]
    public void Composite_ReportsTightestCap_AndPropagatesSet()
    {
        var a = new TokenBucket(new TestClock(), 1000);
        var b = new TokenBucket(new TestClock(), 500);
        var composite = new CompositeRateLimiter(a, b);

        composite.BytesPerSecond.Should().Be(500); // tightest non-unlimited

        composite.BytesPerSecond = 2000;
        a.BytesPerSecond.Should().Be(2000);
        b.BytesPerSecond.Should().Be(2000);
    }

    [Fact]
    public void Composite_AllUnlimited_ReportsZero()
    {
        var composite = new CompositeRateLimiter(
            new TokenBucket(new TestClock(), 0), new TokenBucket(new TestClock(), 0));

        composite.BytesPerSecond.Should().Be(0);
    }
}
