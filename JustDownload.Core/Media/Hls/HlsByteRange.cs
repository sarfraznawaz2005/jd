namespace JustDownload.Core.Media.Hls;

/// <summary>
/// A resolved <c>#EXT-X-BYTERANGE</c> sub-range of a segment's resource (RFC 8216 §4.3.2.2): the
/// <see cref="Length"/> in bytes starting at <see cref="Offset"/>. The offset is always concrete here —
/// <see cref="M3U8Parser"/> resolves the tag's offset-less form (which continues from the previous
/// sub-range of the same resource) while parsing, so an unresolvable range is a parse failure rather than a
/// value with a missing offset.
/// </summary>
/// <param name="Length">The number of bytes the sub-range covers (always positive).</param>
/// <param name="Offset">The absolute offset of the first byte within the resource.</param>
public readonly record struct HlsByteRange(long Length, long Offset)
{
    /// <summary>The inclusive offset of the last byte, as an HTTP <c>Range</c> header expresses it.</summary>
    public long Last => Offset + Length - 1;
}
