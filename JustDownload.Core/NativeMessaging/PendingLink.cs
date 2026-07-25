namespace JustDownload.Core.NativeMessaging;

/// <summary>
/// A link the browser extension handed off while the desktop app was not running (TASK-070). It is queued
/// by the native host and delivered to the app the next time it starts. Carries the auth context captured by
/// the extension (TASK-067) so the eventual download can authenticate.
/// </summary>
public sealed record PendingLink
{
    /// <summary>The URL to download.</summary>
    public required string Url { get; init; }

    /// <summary>The page the link came from (used as the referrer).</summary>
    public string? Referrer { get; init; }

    /// <summary>The serialized Cookie header for the site, if captured.</summary>
    public string? Cookies { get; init; }

    /// <summary>The detected media kind (hls/dash/video/audio), if any.</summary>
    public string? MediaKind { get; init; }

    /// <summary>
    /// Whether <see cref="Url"/> is a *page* to run the extractor pipeline on rather than a direct media URL
    /// (TASK-232). The extension sets this for MediaSource-backed sites it deliberately does not guess at —
    /// YouTube serves everything over SABR, where no fetchable stream URL exists for it to sniff — so the
    /// app resolves the real streams itself via <c>IMediaExtractorRegistry</c>.
    /// </summary>
    public bool Extract { get; init; }
}
