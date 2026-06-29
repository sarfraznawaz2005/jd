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
    public async Task SnapshotDurable_CapturesBeforeFlush_SoTheCheckpointNeverLeadsFsyncedData()
    {
        // TASK-109 AC0: the durable snapshot is what gets persisted as the resume checkpoint. It must be
        // captured BEFORE the fsync runs, so bytes still being written while the flush is in flight are
        // deferred to the next checkpoint rather than recorded ahead of data that is actually on disk.
        var ranges = new ReceivedRanges();
        ranges.Add(0, 100); // [0,99] — written and about to be made durable

        bool flushed = false;
        ranges.SetDurabilityFlush(_ =>
        {
            flushed = true;
            ranges.Add(100, 100); // a write that lands during the fsync — must NOT be in this snapshot
            return Task.CompletedTask;
        });

        IReadOnlyList<ByteInterval> durable = await ranges.SnapshotDurableAsync();

        flushed.Should().BeTrue("the durable snapshot must fsync the file");
        durable.Sum(i => i.Length).Should().Be(100,
            "the snapshot is taken before the flush, so it never records bytes written during/after the fsync");
        durable.Should().ContainSingle().Which.Should().Be(new ByteInterval(0, 99));
    }

    [Fact]
    public async Task SnapshotDurable_WithNoFlushRegistered_IsAPlainSnapshot()
    {
        var ranges = new ReceivedRanges();
        ranges.Add(0, 50);

        IReadOnlyList<ByteInterval> durable = await ranges.SnapshotDurableAsync();

        durable.Should().Equal(ranges.Snapshot());
    }

    [Fact]
    public async Task SnapshotDurable_AfterFlushCleared_DoesNotInvokeTheStaleFlush()
    {
        var ranges = new ReceivedRanges();
        ranges.Add(0, 50);
        int flushes = 0;
        ranges.SetDurabilityFlush(_ => { flushes++; return Task.CompletedTask; });
        ranges.SetDurabilityFlush(null); // the downloader releases the file

        await ranges.SnapshotDurableAsync();

        flushes.Should().Be(0, "a released file handle must never be fsynced");
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
