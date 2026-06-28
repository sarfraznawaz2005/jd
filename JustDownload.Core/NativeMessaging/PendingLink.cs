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
}
