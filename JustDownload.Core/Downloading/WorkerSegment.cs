namespace JustDownload.Core.Downloading;

/// <summary>
/// The live state of one segment being downloaded (TASK-026). <see cref="WriteOffset"/> is advanced only
/// by the owning worker; <see cref="EndInclusive"/> may be lowered by another worker that steals this
/// segment's tail. Both are accessed with volatile/interlocked semantics so the cross-thread truncation
/// is observed promptly and the copy loop can stop at the new boundary.
/// </summary>
internal sealed class WorkerSegment
{
    private long _writeOffset;
    private long _endInclusive;
    private volatile bool _completed;

    public WorkerSegment(int index, SegmentRange range)
    {
        Index = index;
        Start = range.Start;
        _writeOffset = range.Start;
        _endInclusive = range.End;
    }

    /// <summary>This segment's ordinal (initial segments keep their split index; steals get new ones).</summary>
    public int Index { get; }

    /// <summary>The first byte this assignment covers.</summary>
    public long Start { get; }

    /// <summary>The next byte to write (owner-advanced).</summary>
    public long WriteOffset => Volatile.Read(ref _writeOffset);

    /// <summary>The current inclusive end (may be lowered by a steal).</summary>
    public long EndInclusive => Volatile.Read(ref _endInclusive);

    /// <summary>Whether the owning worker has finished (or abandoned) this assignment.</summary>
    public bool Completed => _completed;

    /// <summary>Bytes left to write for this assignment.</summary>
    public long Remaining => EndInclusive - WriteOffset + 1;

    /// <summary>Advances the write offset by <paramref name="count"/>. Called only by the owning worker.</summary>
    public void Advance(long count) => Volatile.Write(ref _writeOffset, _writeOffset + count);

    /// <summary>Lowers the inclusive end (a steal handing the tail to another worker).</summary>
    public void TruncateEnd(long newEnd) => Interlocked.Exchange(ref _endInclusive, newEnd);

    /// <summary>Marks this assignment finished so it is no longer a steal candidate.</summary>
    public void Complete() => _completed = true;
}
