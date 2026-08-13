using System.Text.RegularExpressions;

namespace JustDownload.Core.Media.Extraction;

/// <summary>
/// Makes an extractor's raw failure text safe to show a user (CLAUDE.md §5 — logs and messages redact auth
/// headers, tokens, and signed-URL query strings). Every <see cref="MediaExtractionAttempt"/> reason passes
/// through here at construction, so an unredacted reason cannot exist in the first place.
/// </summary>
internal static partial class ExtractionReasonSanitizer
{
    private const int MaxLength = 160;
    private const string Unknown = "unknown error";

    /// <summary>
    /// Collapses whitespace, strips the query/fragment off every embedded URL (that is where signatures,
    /// tokens and cookies live), and truncates. Never returns an empty string.
    /// </summary>
    public static string Sanitize(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Unknown;
        }

        string collapsed = WhitespaceRegex().Replace(reason, " ").Trim();
        string redacted = UrlRegex().Replace(collapsed, match => StripQuery(match.Value)).Trim();
        if (redacted.Length == 0)
        {
            return Unknown;
        }

        return redacted.Length <= MaxLength ? redacted : string.Concat(redacted.AsSpan(0, MaxLength).TrimEnd(), "…");
    }

    private static string StripQuery(string url)
    {
        int cut = url.AsSpan().IndexOfAny('?', '#');
        return cut < 0 ? url : string.Concat(url.AsSpan(0, cut), "?…");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
}
