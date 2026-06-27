using FluentAssertions;
using JustDownload.Core.Downloading;
using Xunit;

namespace JustDownload.Tests.Downloading;

/// <summary>
/// Unit tests for the pure resume work-planner (TASK-028): distributing connections across the remaining
/// gaps with exact coverage and no piece below the minimum segment size.
/// </summary>
public sealed class SplitRangesTests
{
    private static long Coverage(IEnumerable<SegmentRange> ranges)
    {
        long sum = 0;
        foreach (SegmentRange r in ranges)
        {
            sum += r.Length;
        }

        return sum;
    }

    [Fact]
    public void SingleFullSpanGap_BehavesLikeSplit()
    {
        IReadOnlyList<SegmentRange> result =
            Segmentation.SplitRanges([new SegmentRange(0, 999)], connections: 4, minSegmentSize: 1);

        result.Should().HaveCount(4);
        result[0].Start.Should().Be(0);
        result[^1].End.Should().Be(999);
        Coverage(result).Should().Be(1000);
        AssertContiguous(result);
    }

    [Fact]
    public void MultipleGaps_AreEachCovered_WithoutOverlap()
    {
        var gaps = new[] { new SegmentRange(100, 299), new SegmentRange(400, 999) };
        IReadOnlyList<SegmentRange> result = Segmentation.SplitRanges(gaps, connections: 4, minSegmentSize: 1);

        // Total coverage equals the gap coverage exactly (no byte fetched twice, none missed).
        Coverage(result).Should().Be(Coverage(gaps));

        // Every result range lies within one of the gaps.
        foreach (SegmentRange r in result)
        {
            gaps.Should().Contain(g => r.Start >= g.Start && r.End <= g.End);
        }
    }

    [Fact]
    public void RespectsMinimumSegmentSize()
    {
        // A 1000-byte gap with a 400-byte minimum can be cut at most into 2 pieces, regardless of 8 requested.
        IReadOnlyList<SegmentRange> result =
            Segmentation.SplitRanges([new SegmentRange(0, 999)], connections: 8, minSegmentSize: 400);

        result.Should().HaveCount(2);
        Coverage(result).Should().Be(1000);
        result.Should().OnlyContain(r => r.Length >= 400);
    }

    [Fact]
    public void EmptyGaps_ReturnsEmpty()
    {
        Segmentation.SplitRanges([], connections: 4, minSegmentSize: 1).Should().BeEmpty();
    }

    private static void AssertContiguous(IReadOnlyList<SegmentRange> ranges)
    {
        for (int i = 1; i < ranges.Count; i++)
        {
            ranges[i].Start.Should().Be(ranges[i - 1].End + 1);
        }
    }
}
