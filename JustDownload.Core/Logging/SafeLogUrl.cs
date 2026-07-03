namespace JustDownload.Core.Logging;

/// <summary>
/// Reduces a URL to a log-safe form — scheme + host only — so signed-URL query strings, tokens, and any
/// userinfo never reach a log, regardless of the (often non-standard) token parameter name (CLAUDE.md §5,
/// TASK-099). This is defence-in-depth at the log site, independent of the redacting logger's pattern list.
/// </summary>
internal static class SafeLogUrl
{
    /// <summary>Returns <c>scheme://host</c> for an absolute URL, or a constant placeholder otherwise.</summary>
    public static string Of(string? url) =>
        // On Unix, Uri.TryCreate(..., UriKind.Absolute, ...) treats a leading-slash string like "/relative/path"
        // as a valid absolute *file* URI (it looks like an absolute filesystem path) — Windows has no such
        // coercion (there's no drive letter), so the exact same input parses as non-absolute there. Every real
        // caller here only ever passes a download URL (http/https/ftp), never a local path, so excluding the
        // file scheme keeps this platform-independent instead of silently mislabeling a malformed URL as one.
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme != Uri.UriSchemeFile
            ? $"{uri.Scheme}://{uri.Host}"
            : "(non-absolute URL)";
}
