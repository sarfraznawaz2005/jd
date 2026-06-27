namespace JustDownload.Core.Downloading;

/// <summary>An inclusive byte interval <c>[Start, EndInclusive]</c> that has been written to disk (TASK-028).</summary>
public readonly record struct ByteInterval(long Start, long EndInclusive)
{
    /// <summary>The number of bytes the interval spans.</summary>
    public long Length => EndInclusive - Start + 1;
}

/// <summary>
/// The set of byte ranges already written to the output file (TASK-028, US-2). This is the resume
/// checkpoint decoupled from how the bytes were fetched: workers record each committed write here, and the
/// manager persists the coalesced intervals so a pause/crash can resume by downloading only the
/// <see cref="Gaps"/> — already-downloaded bytes are never re-fetched. Coalescing keeps the set to roughly
/// one interval per active connection, so it stays tiny and cheap to persist.
/// <para>Thread-safe: the segmented downloader records writes from several worker threads concurrently.</para>
/// </summary>
public sealed class ReceivedRanges
{
    private readonly object _gate = new();
    private readonly List<ByteInterval> _intervals = [];
    private long _total;

    /// <summary>Creates an empty set (a fresh download).</summary>
    public ReceivedRanges()
    {
    }

    /// <summary>Creates a set seeded with previously-received intervals (a resume).</summary>
    public ReceivedRanges(IEnumerable<ByteInterval> seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        foreach (ByteInterval interval in seed)
        {
            if (interval.Length > 0)
            {
                AddInternal(interval.Start, interval.EndInclusive);
            }
        }
    }

    /// <summary>Total bytes received across all (coalesced, non-overlapping) intervals.</summary>
    public long TotalReceived
    {
        get
        {
            lock (_gate)
            {
                return _total;
            }
        }
    }

    /// <summary>Records that <paramref name="length"/> bytes were written starting at <paramref name="start"/>.</summary>
    public void Add(long start, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        if (length <= 0)
        {
            return;
        }

        lock (_gate)
        {
            AddInternal(start, start + length - 1);
        }
    }

    /// <summary>A snapshot of the coalesced received intervals, ordered by start.</summary>
    public IReadOnlyList<ByteInterval> Snapshot()
    {
        lock (_gate)
        {
            return _intervals.ToArray();
        }
    }

    /// <summary>
    /// The not-yet-received ranges within <c>[0, totalLength)</c>, ordered by start — the work a resume must
    /// still fetch. Returns a single full-span range for an empty set, and nothing when complete.
    /// </summary>
    public IReadOnlyList<SegmentRange> Gaps(long totalLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalLength);

        var gaps = new List<SegmentRange>();
        lock (_gate)
        {
            long cursor = 0;
            foreach (ByteInterval interval in _intervals)
            {
                if (interval.Start > cursor)
                {
                    gaps.Add(new SegmentRange(cursor, Math.Min(interval.Start, totalLength) - 1));
                }

                cursor = Math.Max(cursor, interval.EndInclusive + 1);
                if (cursor >= totalLength)
                {
                    break;
                }
            }

            if (cursor < totalLength)
            {
                gaps.Add(new SegmentRange(cursor, totalLength - 1));
            }
        }

        return gaps;
    }

    // Inserts [start, end] and merges any overlapping/adjacent intervals, keeping the list sorted and
    // disjoint. The list holds ~one interval per active connection, so the linear rebuild is cheap.
    private void AddInternal(long start, long end)
    {
        _intervals.Add(new ByteInterval(start, end));
        _intervals.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        var merged = new List<ByteInterval>(_intervals.Count);
        ByteInterval current = _intervals[0];
        for (int i = 1; i < _intervals.Count; i++)
        {
            ByteInterval next = _intervals[i];
            if (next.Start <= current.EndInclusive + 1)
            {
                // Overlapping or directly adjacent — extend the current run.
                current = current with { EndInclusive = Math.Max(current.EndInclusive, next.EndInclusive) };
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }

        merged.Add(current);

        _intervals.Clear();
        _intervals.AddRange(merged);

        long total = 0;
        foreach (ByteInterval interval in _intervals)
        {
            total += interval.Length;
        }

        _total = total;
    }
}
