namespace JustDownload.Core.Media.Hls;

/// <summary>The encryption method declared by an HLS <c>#EXT-X-KEY</c> (TASK-037).</summary>
public enum HlsKeyMethod
{
    /// <summary>No encryption — segments are downloaded as-is.</summary>
    None = 0,

    /// <summary>AES-128 in CBC mode with PKCS7 padding (the only encryption JustDownload decrypts).</summary>
    Aes128 = 1,

    /// <summary>SAMPLE-AES — recognised but not supported (would need per-sample handling).</summary>
    SampleAes = 2,
}

/// <summary>
/// A parsed <c>#EXT-X-KEY</c> (TASK-037 AC2). For <see cref="HlsKeyMethod.Aes128"/> it carries the absolute
/// key <see cref="Uri"/> to fetch the 16-byte key from, and the explicit initialisation vector when the tag
/// supplied one. When no <c>IV</c> attribute is present the segment's media sequence number is used as the
/// IV (RFC 8216 §5.2), which the downloader derives per segment.
/// </summary>
/// <param name="Method">The declared encryption method.</param>
/// <param name="Uri">The absolute key URI (resolved against the playlist), or <see langword="null"/> for <see cref="HlsKeyMethod.None"/>.</param>
/// <param name="Iv">The explicit 16-byte IV, or <see langword="null"/> to derive it from the media sequence.</param>
public sealed record HlsEncryption(HlsKeyMethod Method, Uri? Uri, IReadOnlyList<byte>? Iv)
{
    /// <summary>A shared "no encryption" instance.</summary>
    public static HlsEncryption None { get; } = new(HlsKeyMethod.None, null, null);
}
