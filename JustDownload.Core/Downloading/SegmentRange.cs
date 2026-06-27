namespace JustDownload.Core.Downloading;

/// <summary>
/// A contiguous, inclusive byte range owned by one download connection (TASK-026). Both ends are
/// concrete (unlike a request <c>ByteRange</c>, whose end may be open) because a segment always covers a
/// known span of a sized resource.
/// </summary>
/// <param name="Start">The inclusive first byte.</param>
/// <param name="End">The inclusive last byte.</param>
public readonly record struct SegmentRange(long Start, long End)
{
    /// <summary>The number of bytes in the range.</summary>
    public long Length => End - Start + 1;
}
