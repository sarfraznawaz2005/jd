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
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? $"{uri.Scheme}://{uri.Host}" : "(non-absolute URL)";
}
