using FluentAssertions;
using JustDownload.Core.Downloading;
using Xunit;

namespace JustDownload.Tests.Downloading;

/// <summary>
/// Unit tests for the pure resume-checkpoint interval set (TASK-028): coalescing, total accounting, and gap
/// computation — the maths a correct resume depends on.
/// </summary>
public sealed class ReceivedRangesTests
{
    [Fact]
    public void Add_CoalescesOverlappingAndAdjacentIntervals()
    {
        var ranges = new ReceivedRanges();
        ranges.Add(0, 100);     // [0,99]
        ranges.Add(100, 50);    // [100,149] adjacent → merges
        ranges.Add(140, 60);    // [140,199] overlaps → merges

        ranges.Snapshot().Should().ContainSingle().Which.Should().Be(new ByteInterval(0, 199));
        ranges.TotalReceived.Should().Be(200);
    }

    [Fact]
    public void Add_KeepsDisjointIntervalsSeparate()
    {
        var ranges = new ReceivedRanges();
        ranges.Add(0, 100);     // [0,99]
        ranges.Add(200, 100);   // [200,299] — gap at [100,199]

        ranges.Snapshot().Should().Equal(new ByteInterval(0, 99), new ByteInterval(200, 299));
        ranges.TotalReceived.Should().Be(200);
    }

    [Fact]
    public void Gaps_EmptySet_IsWholeSpan()
    {
        var ranges = new ReceivedRanges();
        ranges.Gaps(1000).Should().ContainSingle().Which.Should().Be(new SegmentRange(0, 999));
    }

    [Fact]
    public void Gaps_PartialSet_ReturnsHoles()
    {
        var ranges = new ReceivedRanges();
        ranges.Add(0, 100);     // [0,99]
        ranges.Add(300, 100);   // [300,399]

        // Holes within [0,1000): [100,299] and [400,999].
        ranges.Gaps(1000).Should().Equal(new SegmentRange(100, 299), new SegmentRange(400, 999));
    }

    [Fact]
    public void Gaps_FullSet_IsEmpty()
    {
        var ranges = new ReceivedRanges();
        ranges.Add(0, 1000);
        ranges.Gaps(1000).Should().BeEmpty();
    }

    [Fact]
    public void Constructor_SeedsFromIntervals()
    {
        var ranges = new ReceivedRanges([new ByteInterval(0, 99), new ByteInterval(200, 299)]);
        ranges.TotalReceived.Should().Be(200);
        ranges.Gaps(300).Should().ContainSingle().Which.Should().Be(new SegmentRange(100, 199));
    }
}
