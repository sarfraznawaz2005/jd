namespace JustDownload.Core.Downloading;

/// <summary>
/// A live progress report for a single connection (worker) of a segmented download (TASK-054, US-15c). The
/// segmented downloader emits these as its workers write, so the UI can show what each connection is doing:
/// which byte range it currently owns and how far through it is. <see cref="ConnectionId"/> is stable for the
/// life of a worker (it keeps its id across work-steals), while <see cref="SegmentIndex"/> changes each time
/// the worker picks up (or steals) a new segment. Per-connection <i>speed</i> is derived by the consumer from
/// the stream of these reports, keeping the engine free of timing concerns.
/// </summary>
public sealed record ConnectionProgress
{
    /// <summary>The stable id of the connection/worker (0-based; survives work-steals).</summary>
    public required int ConnectionId { get; init; }

    /// <summary>The index of the segment this connection is currently working (changes on each steal).</summary>
    public required int SegmentIndex { get; init; }

    /// <summary>The first byte of the current segment.</summary>
    public required long Start { get; init; }

    /// <summary>The inclusive last byte of the current segment.</summary>
    public required long End { get; init; }

    /// <summary>The next byte this connection will write (its progress cursor within the segment).</summary>
    public required long Position { get; init; }

    /// <summary>Whether this connection has finished all its work and will report no more.</summary>
    public bool IsComplete { get; init; }

    /// <summary>Bytes written so far for the current segment.</summary>
    public long SegmentDownloaded => Math.Max(0, Position - Start);

    /// <summary>Total bytes in the current segment.</summary>
    public long SegmentTotal => Math.Max(0, End - Start + 1);
}
