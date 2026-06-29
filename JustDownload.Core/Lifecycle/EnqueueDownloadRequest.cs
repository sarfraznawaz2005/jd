using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Settings;
using JustDownload.Core.Transport.Proxy;

namespace JustDownload.Core.Lifecycle;

/// <summary>
/// The information needed to add a new download to the queue (TASK-031). The destination directory and file
/// name are resolved by the caller (the App resolves a server-suggested name via the transport before
/// enqueuing), so the manager can form a concrete path and the record is persistable up front.
/// </summary>
public sealed record EnqueueDownloadRequest
{
    /// <summary>The source URL.</summary>
    public required Uri Url { get; init; }

    /// <summary>The directory the file is written to.</summary>
    public required string DestinationDirectory { get; init; }

    /// <summary>The target file name within <see cref="DestinationDirectory"/>.</summary>
    public required string FileName { get; init; }

    /// <summary>Total size in bytes when already known (e.g. from a prior probe); otherwise <see langword="null"/>.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>The referring page URL, sent as <c>Referer</c> and used by renew flows.</summary>
    public string? Referrer { get; init; }

    /// <summary>
    /// Request cookies captured by the browser extension for an authenticated/signed download (TASK-091),
    /// as a <c>Cookie</c> header value. Persisted only in the OS keychain (never SQLite), so a cookie-gated
    /// hand-off succeeds. <see langword="null"/> for ordinary downloads.
    /// </summary>
    public string? Cookies { get; init; }

    /// <summary>The file-type category code (Video, Document, …) for auto-organization.</summary>
    public string? CategoryType { get; init; }

    /// <summary>Per-download maximum connection count; <see langword="null"/> uses the engine default.</summary>
    public int? MaxConnections { get; init; }

    /// <summary>Per-download speed cap in bytes/second; <see langword="null"/>/<c>0</c> means unlimited.</summary>
    public long? SpeedLimit { get; init; }

    /// <summary>
    /// A proxy that routes only this download, overriding the global proxy (TASK-153). <see langword="null"/>
    /// (or a <see cref="ProxyKind.None"/> configuration) means "use the global proxy". When the override
    /// carries credentials, the password travels here as plaintext only long enough for the manager to store
    /// it in the OS keychain (§5); it is never persisted in the clear.
    /// </summary>
    public ProxyConfiguration? Proxy { get; init; }

    /// <summary>
    /// The media download path for this download (TASK-154): <see cref="MediaKind.Hls"/> routes the start
    /// through the media coordinator (segments &#8594; concat); <see langword="null"/> or
    /// <see cref="MediaKind.Progressive"/> is a plain segmented-HTTP download. <see cref="Url"/> is the chosen
    /// media playlist/variant URL.
    /// </summary>
    public MediaKind? MediaKind { get; init; }

    /// <summary>
    /// The audio stream URL for a separate-streams/DASH media download (TASK-154); <see cref="Url"/> is the
    /// video stream. <see langword="null"/> for HLS/progressive.
    /// </summary>
    public Uri? MediaAudioUrl { get; init; }

    /// <summary>The preferred output container for a muxed media download (TASK-154); <see langword="null"/> = muxer default.</summary>
    public MediaContainer? MediaContainer { get; init; }
}
