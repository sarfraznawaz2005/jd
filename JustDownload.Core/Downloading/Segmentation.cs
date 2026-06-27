namespace JustDownload.Core.Downloading;

/// <summary>
/// A snapshot of one in-progress segment for the work-stealing decision (TASK-026): its identifying
/// <see cref="Index"/>, the next byte it will write (<see cref="NextOffset"/>), and its current
/// inclusive end. Remaining work is <c>EndInclusive - NextOffset + 1</c>.
/// </summary>
public readonly record struct StealCandidate(int Index, long NextOffset, long EndInclusive);

/// <summary>
/// The outcome of a work-steal (TASK-026): the victim segment (<see cref="VictimIndex"/>) should be
/// truncated to <see cref="NewVictimEnd"/>, and the idle connection takes over
/// <see cref="StolenRange"/> (the tail half of what the victim had left).
/// </summary>
public readonly record struct StealPlan(int VictimIndex, long NewVictimEnd, SegmentRange StolenRange);

/// <summary>
/// The pure, deterministic segmentation maths at the heart of the engine (TASK-026, CLAUDE.md §1 / §5).
/// <see cref="Split"/> divides a sized resource into contiguous ranges; <see cref="PlanSteal"/> decides
/// how an idle connection re-splits the largest remaining range. Both are side-effect-free so they are
/// exhaustively unit-testable in isolation (US-1 AC).
/// </summary>
public static class Segmentation
{
    /// <summary>The minimum number of connections.</summary>
    public const int MinConnections = 1;

    /// <summary>The maximum number of connections (US-1: configurable 1–32).</summary>
    public const int MaxConnections = 32;

    /// <summary>
    /// Splits <paramref name="totalLength"/> into up to <paramref name="connections"/> contiguous
    /// inclusive ranges that exactly cover <c>[0, totalLength)</c>, as evenly as possible (any remainder
    /// is spread one byte at a time across the leading segments). The connection count is clamped to
    /// 1–32, then reduced so no segment is smaller than <paramref name="minSegmentSize"/> (and never more
    /// segments than bytes).
    /// </summary>
    public static IReadOnlyList<SegmentRange> Split(long totalLength, int connections, long minSegmentSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minSegmentSize);

        int byRequest = Math.Clamp(connections, MinConnections, MaxConnections);
        long bySize = Math.Max(1, totalLength / minSegmentSize);
        int count = (int)Math.Min(Math.Min(byRequest, bySize), totalLength);
        count = Math.Max(count, 1);

        long baseSize = totalLength / count;
        long remainder = totalLength % count;

        var ranges = new List<SegmentRange>(count);
        long start = 0;
        for (int i = 0; i < count; i++)
        {
            long size = baseSize + (i < remainder ? 1 : 0);
            ranges.Add(new SegmentRange(start, start + size - 1));
            start += size;
        }

        return ranges;
    }

    /// <summary>
    /// Chooses the work-steal: the candidate with the most remaining bytes is split in half — the victim
    /// keeps the front half and the idle connection takes the tail half. Returns <see langword="null"/>
    /// when the largest remaining range is smaller than twice <paramref name="minStealSize"/> (so neither
    /// half would be worth a new connection), which is the signal for the idle connection to simply stop.
    /// </summary>
    public static StealPlan? PlanSteal(IReadOnlyList<StealCandidate> candidates, long minStealSize)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minStealSize);

        StealCandidate? victim = null;
        long bestRemaining = 0;
        foreach (StealCandidate candidate in candidates)
        {
            long remaining = candidate.EndInclusive - candidate.NextOffset + 1;
            if (remaining > bestRemaining)
            {
                bestRemaining = remaining;
                victim = candidate;
            }
        }

        if (victim is not { } chosen || bestRemaining < 2 * minStealSize)
        {
            return null;
        }

        long splitPoint = chosen.NextOffset + bestRemaining / 2;
        return new StealPlan(
            chosen.Index,
            splitPoint - 1,
            new SegmentRange(splitPoint, chosen.EndInclusive));
    }
}
