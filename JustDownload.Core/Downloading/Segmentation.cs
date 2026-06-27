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
    /// Distributes up to <paramref name="connections"/> worker segments across a set of disjoint
    /// <paramref name="gaps"/> (the not-yet-downloaded ranges of a resumed download), proportional to each
    /// gap's size, then splits each gap evenly into its share. Every gap gets at least one segment so it is
    /// always covered; extra connections go to the largest pieces, never splitting below
    /// <paramref name="minSegmentSize"/>. With a single full-span gap this reduces to <see cref="Split"/>.
    /// Pure and deterministic (US-2 resume planning).
    /// </summary>
    public static IReadOnlyList<SegmentRange> SplitRanges(
        IReadOnlyList<SegmentRange> gaps,
        int connections,
        long minSegmentSize)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minSegmentSize);

        if (gaps.Count == 0)
        {
            return [];
        }

        int budget = Math.Clamp(connections, MinConnections, MaxConnections);
        int n = gaps.Count;
        var pieces = new int[n];
        Array.Fill(pieces, 1);
        int used = n;

        // Hand out the remaining connections to whichever gap currently has the largest piece, provided one
        // more cut still leaves both pieces at or above the minimum segment size.
        while (used < budget)
        {
            int best = -1;
            long bestPieceSize = 0;
            for (int i = 0; i < n; i++)
            {
                long len = gaps[i].Length;
                long currentPiece = len / pieces[i];
                long nextPiece = len / (pieces[i] + 1);
                if (nextPiece >= minSegmentSize && currentPiece > bestPieceSize)
                {
                    bestPieceSize = currentPiece;
                    best = i;
                }
            }

            if (best < 0)
            {
                break; // No gap can be split further without going below the minimum.
            }

            pieces[best]++;
            used++;
        }

        var ranges = new List<SegmentRange>(used);
        for (int i = 0; i < n; i++)
        {
            SplitOne(gaps[i], pieces[i], ranges);
        }

        return ranges;
    }

    // Splits one inclusive range into `count` contiguous pieces, spreading any remainder one byte at a time
    // across the leading pieces (matching Split's even-division behaviour).
    private static void SplitOne(SegmentRange range, int count, List<SegmentRange> into)
    {
        long length = range.Length;
        long baseSize = length / count;
        long remainder = length % count;
        long start = range.Start;
        for (int k = 0; k < count; k++)
        {
            long size = baseSize + (k < remainder ? 1 : 0);
            into.Add(new SegmentRange(start, start + size - 1));
            start += size;
        }
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
