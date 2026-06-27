namespace JustDownload.Core.Downloading;

/// <summary>
/// Shared, thread-safe state for a segmented download (TASK-026): the live segment list, the running
/// byte count, and the steal counter. <see cref="TrySteal"/> is the synchronized decision an idle
/// connection makes — it picks the largest remaining range via the pure <see cref="Segmentation.PlanSteal"/>,
/// truncates that victim, and hands the tail back as a fresh segment.
/// </summary>
internal sealed class DownloadState
{
    private readonly object _gate = new();
    private readonly List<WorkerSegment> _segments;
    private int _nextIndex;
    private int _steals;
    private long _bytesWritten;

    public DownloadState(IReadOnlyList<SegmentRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        _segments = new List<WorkerSegment>(ranges.Count);
        for (int i = 0; i < ranges.Count; i++)
        {
            _segments.Add(new WorkerSegment(i, ranges[i]));
        }

        _nextIndex = ranges.Count;
        InitialSegments = _segments.ToArray();
    }

    /// <summary>The segments to spawn a worker for (the initial split).</summary>
    public IReadOnlyList<WorkerSegment> InitialSegments { get; }

    /// <summary>How many steals have occurred.</summary>
    public int Steals => Volatile.Read(ref _steals);

    /// <summary>Total bytes written so far across all segments.</summary>
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    /// <summary>Adds to the running byte total and returns the new value.</summary>
    public long AddBytes(long count) => Interlocked.Add(ref _bytesWritten, count);

    /// <summary>
    /// Attempts a work-steal: under the lock, finds the active segment with the most remaining bytes and,
    /// if it is large enough to split, truncates it and returns a new segment covering its tail. Returns
    /// <see langword="null"/> when nothing is worth stealing (the idle worker then stops).
    /// </summary>
    public WorkerSegment? TrySteal(long minStealSize)
    {
        lock (_gate)
        {
            var candidates = new List<StealCandidate>();
            var map = new List<WorkerSegment>();
            foreach (WorkerSegment segment in _segments)
            {
                if (segment.Completed)
                {
                    continue;
                }

                long offset = segment.WriteOffset;
                long end = segment.EndInclusive;
                if (end - offset + 1 > 0)
                {
                    candidates.Add(new StealCandidate(map.Count, offset, end));
                    map.Add(segment);
                }
            }

            if (Segmentation.PlanSteal(candidates, minStealSize) is not { } plan)
            {
                return null;
            }

            WorkerSegment victim = map[plan.VictimIndex];

            // The victim advances outside this lock; if it has already passed the split point the plan is
            // stale — skip rather than truncate behind the victim.
            if (victim.WriteOffset > plan.NewVictimEnd)
            {
                return null;
            }

            victim.TruncateEnd(plan.NewVictimEnd);
            var stolen = new WorkerSegment(_nextIndex++, plan.StolenRange);
            _segments.Add(stolen);
            _steals++;
            return stolen;
        }
    }
}
