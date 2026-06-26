namespace JustDownload.Core.Data.Models;

/// <summary>
/// A persisted segment row (PRD §4.4 <c>segments</c> table): one connection's byte range within a
/// download and how much of it has been written. These rows are the checkpoint the crash-resume
/// contract depends on — restarting re-issues <c>Range</c> requests from <see cref="Downloaded"/>
/// so already-fetched bytes are never re-downloaded (CLAUDE.md §5, US-2).
/// </summary>
public sealed record DownloadSegment
{
    /// <summary>The auto-increment primary key. <c>0</c> for a not-yet-inserted record.</summary>
    public long Id { get; init; }

    /// <summary>The owning download's id (foreign key, cascade-deleted with the download).</summary>
    public long DownloadId { get; init; }

    /// <summary>The segment's ordinal within the download (0-based).</summary>
    public int Index { get; init; }

    /// <summary>The inclusive start byte offset of this segment's range.</summary>
    public long Start { get; init; }

    /// <summary>The inclusive end byte offset of this segment's range.</summary>
    public long End { get; init; }

    /// <summary>The number of bytes of this segment already written to disk.</summary>
    public long Downloaded { get; init; }

    /// <summary>The segment's state code (e.g. <c>pending</c>, <c>active</c>, <c>complete</c>).</summary>
    public required string State { get; init; }
}
