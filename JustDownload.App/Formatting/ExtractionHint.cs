using JustDownload.Core.Media.Extraction;

namespace JustDownload.App.Formatting;

/// <summary>Which actionable hint (if any) to show for a failed media extraction.</summary>
public enum ExtractionHintKind
{
    /// <summary>No known, actionable pattern was recognised — show nothing extra.</summary>
    None = 0,

    /// <summary>yt-dlp is missing a JavaScript runtime (deno) it needs for some sites.</summary>
    InstallJsRuntime,

    /// <summary>yt-dlp hit bot-detection / rate-limiting and cookies may help.</summary>
    TryCookies,
}

/// <summary>
/// A clickable hint shown below a raw extraction failure when its reason matches a known, actionable
/// pattern. Lives in the App (D5: Core stays display-free); <see cref="ExtractionHintClassifier"/> builds
/// it from the structured <see cref="MediaExtractionAttempt"/> list. The <see cref="Reason"/> it matches on
/// is already redacted (CLAUDE.md §5) by <c>ExtractionReasonSanitizer</c>, so this never re-sanitizes.
/// </summary>
public sealed record ExtractionHint(ExtractionHintKind Kind, string Text, string? ActionUri);

/// <summary>
/// Maps a failed extraction onto a single actionable hint, or <see langword="null"/> when no known pattern
/// matches (a plain decline, or an unrelated failure). Pure and dependency-free: it only reads the
/// (already redacted) attempt reasons and matches case-insensitive substrings. JS-runtime takes priority over
/// the bot/cookies hint when both somehow appear.
/// </summary>
public static class ExtractionHintClassifier
{
    private const string JsRuntime = "javascript runtime";
    private const string JsRuntimeShort = "js runtime";
    private const string Deno = "deno";
    private const string BotChallenge = "confirm you're not a bot";
    private const string TooManyRequests = "too many requests";
    private const string Status429 = "429";
    private const string Cookies = "cookies";

    /// <summary>Where to send the user to install a JavaScript runtime.</summary>
    public const string DenoUrl = "https://deno.land/";

    /// <summary>Where to send the user for yt-dlp's cookies documentation.</summary>
    public const string YtDlpCookiesUrl = "https://github.com/yt-dlp/yt-dlp#cookies";

    public static ExtractionHint? Classify(IReadOnlyList<MediaExtractionAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        // Two passes so a JS-runtime signal is never shadowed by an earlier bot/cookies reason when both
        // appear among the attempts.
        ExtractionHint? js = Match(attempts, IsJsRuntime);
        if (js is not null)
        {
            return js;
        }

        return Match(attempts, IsBotOrCookies);
    }

    private static ExtractionHint? Match(
        IReadOnlyList<MediaExtractionAttempt> attempts, Func<string?, bool> predicate)
    {
        foreach (MediaExtractionAttempt attempt in attempts)
        {
            if (attempt.Outcome is not (MediaExtractionOutcome.Failed or MediaExtractionOutcome.NetworkFailure))
            {
                continue;
            }

            if (predicate(attempt.Reason))
            {
                return IsJsRuntime(attempt.Reason)
                    ? new ExtractionHint(
                        ExtractionHintKind.InstallJsRuntime,
                        "yt-dlp needs a JavaScript runtime (deno) to extract some sites — install it, then retry.",
                        DenoUrl)
                    : new ExtractionHint(
                        ExtractionHintKind.TryCookies,
                        "This looks like bot-detection or rate-limiting. Add cookies in yt-dlp (Settings → Video, once that UI lands) — see yt-dlp's cookies guide.",
                        YtDlpCookiesUrl);
            }
        }

        return null;
    }

    private static bool IsJsRuntime(string? reason) =>
        reason is not null
        && (Contains(reason, JsRuntime) || Contains(reason, JsRuntimeShort) || Contains(reason, Deno));

    private static bool IsBotOrCookies(string? reason) =>
        reason is not null
        && (Contains(reason, BotChallenge) || Contains(reason, TooManyRequests)
            || Contains(reason, Status429) || Contains(reason, Cookies));

    private static bool Contains(string reason, string needle) =>
        reason.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
