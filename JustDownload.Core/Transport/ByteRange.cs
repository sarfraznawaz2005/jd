namespace JustDownload.Core.Transport;

/// <summary>
/// A half-open-aware byte range for a ranged request (TASK-023). <see cref="From"/> is the inclusive
/// first byte; <see cref="To"/> is the inclusive last byte, or <see langword="null"/> for "to the end of
/// the resource" (an open-ended <c>bytes=from-</c> request). This is the unit the segmentation engine
/// (TASK-026) hands to the transport for each connection.
/// </summary>
/// <param name="From">The inclusive start offset (must be non-negative).</param>
/// <param name="To">The inclusive end offset, or <see langword="null"/> for open-ended.</param>
public readonly record struct ByteRange(long From, long? To)
{
    /// <summary>The number of bytes the range covers, or <see langword="null"/> when open-ended.</summary>
    public long? Length => To is { } to ? to - From + 1 : null;
}
