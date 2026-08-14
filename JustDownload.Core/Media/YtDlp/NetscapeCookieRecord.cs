namespace JustDownload.Core.Media.YtDlp;

/// <summary>
/// One cookie as captured from a browser session, in the OS/engine-agnostic shape
/// <see cref="NetscapeCookieFileWriter"/> serializes to yt-dlp's Netscape cookie-file format
/// (<c>--cookies &lt;path&gt;</c>). Deliberately independent of any specific browser-embedding API (e.g.
/// WebView2's <c>CoreWebView2Cookie</c>) so the mapping and file format stay unit-testable in
/// <c>JustDownload.Core</c> without that dependency (D5).
/// </summary>
/// <param name="Domain">
/// The cookie's domain exactly as the browser reports it — Chromium-based engines already prefix this
/// with a leading <c>.</c> for a domain-wide (non host-only) cookie, matching the Netscape format's own
/// convention, so no extra normalization is needed here.
/// </param>
/// <param name="IncludeSubdomains">Netscape format's second column: whether the cookie applies to subdomains too.</param>
/// <param name="Path">The cookie's path scope.</param>
/// <param name="Secure">Whether the cookie is HTTPS-only.</param>
/// <param name="ExpiresUnixSeconds">
/// Expiry as Unix seconds, or 0 for a session-only cookie (Netscape format's convention for "no expiry").
/// </param>
/// <param name="Name">The cookie name.</param>
/// <param name="Value">The cookie value.</param>
public readonly record struct NetscapeCookieRecord(
    string Domain,
    bool IncludeSubdomains,
    string Path,
    bool Secure,
    long ExpiresUnixSeconds,
    string Name,
    string Value);
