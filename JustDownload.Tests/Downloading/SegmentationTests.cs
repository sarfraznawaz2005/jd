using FluentAssertions;
using JustDownload.Core.Downloading;
using Xunit;

namespace JustDownload.Tests.Downloading;

/// <summary>
/// Unit tests for the pure segmentation maths (TASK-026 AC2): the N-way split (AC0) and the
/// work-steal selection that re-splits the largest remaining range (AC1).
/// </summary>
public sealed class SegmentationTests
{
    private static void AssertCoversContiguously(IReadOnlyList<SegmentRange> ranges, long total)
    {
        ranges.Should().NotBeEmpty();
        ranges[0].Start.Should().Be(0);
        ranges[^1].End.Should().Be(total - 1);
        for (int i = 1; i < ranges.Count; i++)
        {
            ranges[i].Start.Should().Be(ranges[i - 1].End + 1, "segments must be contiguous with no gaps/overlap");
        }

        ranges.Sum(r => r.Length).Should().Be(total);
    }

    [Theory]
    [InlineData(1000, 4, 4)]
    [InlineData(1000, 1, 1)]
    [InlineData(1003, 4, 4)]
    public void Split_ProducesContiguousCoverage(long total, int connections, int expectedCount)
    {
        IReadOnlyList<SegmentRange> ranges = Segmentation.Split(total, connections, minSegmentSize: 1);
        ranges.Should().HaveCount(expectedCount);
        AssertCoversContiguously(ranges, total);
    }

    [Fact]
    public void Split_DistributesRemainder_ToLeadingSegments()
    {
        // 1003 / 4 = 250 r3 → first three are 251, last is 250.
        IReadOnlyList<SegmentRange> ranges = Segmentation.Split(1003, 4, minSegmentSize: 1);
        ranges.Select(r => r.Length).Should().ContainInOrder(251, 251, 251, 250);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(0)]
    [InlineData(-5)]
    public void Split_ClampsConnections_Between1And32(int requested)
    {
        IReadOnlyList<SegmentRange> ranges = Segmentation.Split(1_000_000, requested, minSegmentSize: 1);
        ranges.Count.Should().BeInRange(Segmentation.MinConnections, Segmentation.MaxConnections);
        AssertCoversContiguously(ranges, 1_000_000);
    }

    [Fact]
    public void Split_RespectsMinSegmentSize()
    {
        // 10 bytes with a 4-byte floor → at most 2 segments even though 8 were requested.
        IReadOnlyList<SegmentRange> ranges = Segmentation.Split(10, 8, minSegmentSize: 4);
        ranges.Should().HaveCount(2);
        AssertCoversContiguously(ranges, 10);
    }

    [Fact]
    public void Split_NeverMakesMoreSegmentsThanBytes()
    {
        IReadOnlyList<SegmentRange> ranges = Segmentation.Split(3, 8, minSegmentSize: 1);
        ranges.Should().HaveCount(3);
    }

    [Fact]
    public void PlanSteal_PicksLargestRemaining_AndSplitsTailInHalf()
    {
        // AC1: three active segments; the second has the most left (1000 bytes) → it is the victim.
        var candidates = new List<StealCandidate>
        {
            new(Index: 0, NextOffset: 950, EndInclusive: 999),    // 50 remaining
            new(Index: 1, NextOffset: 1000, EndInclusive: 1999),  // 1000 remaining (largest)
            new(Index: 2, NextOffset: 2500, EndInclusive: 2599),  // 100 remaining
        };

        StealPlan? plan = Segmentation.PlanSteal(candidates, minStealSize: 1);

        plan.Should().NotBeNull();
        plan!.Value.VictimIndex.Should().Be(1);
        // remaining 1000 → splitPoint = 1000 + 500 = 1500; victim keeps [1000,1499], stealer takes [1500,1999].
        plan.Value.NewVictimEnd.Should().Be(1499);
        plan.Value.StolenRange.Should().Be(new SegmentRange(1500, 1999));
        // The two halves are contiguous and exactly cover the victim's original remaining range.
        (plan.Value.NewVictimEnd + 1).Should().Be(plan.Value.StolenRange.Start);
        plan.Value.StolenRange.End.Should().Be(candidates[1].EndInclusive);
    }

    [Fact]
    public void PlanSteal_ReturnsNull_WhenLargestRemainingTooSmall()
    {
        var candidates = new List<StealCandidate>
        {
            new(0, NextOffset: 0, EndInclusive: 99),   // 100 remaining
            new(1, NextOffset: 0, EndInclusive: 199),  // 200 remaining
        };

        // minStealSize 200 → needs ≥400 remaining to split; nothing qualifies.
        Segmentation.PlanSteal(candidates, minStealSize: 200).Should().BeNull();
    }

    [Fact]
    public void PlanSteal_ReturnsNull_WhenNoCandidates()
    {
        Segmentation.PlanSteal([], minStealSize: 1).Should().BeNull();
    }
}
