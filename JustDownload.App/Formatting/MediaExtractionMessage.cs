using System.Globalization;
using JustDownload.Core.Media.Extraction;

namespace JustDownload.App.Formatting;

/// <summary>
/// Turns the engine's structured <see cref="MediaExtractionAttempt"/> list into the one line the dialogs
/// show. Lives in the App because <c>JustDownload.Core</c> stays free of display concerns (D5): Core says
/// <em>what</em> happened, this says how to word it. Reasons arrive already redacted (CLAUDE.md §5).
/// </summary>
public static class MediaExtractionMessage
{
    /// <summary>Shown when every extractor genuinely declined — nothing here looks downloadable.</summary>
    public const string NoMediaFound = "Couldn't find downloadable media at this URL.";

    // Two attributed reasons is as much as the footer status text can carry without dominating the dialog.
    private const int MaxReasons = 2;

    /// <summary>
    /// The message for a failed extraction: a connectivity message when any extractor could not reach the
    /// host, the extractors' own reasons when they recognised the URL but failed, otherwise
    /// <see cref="NoMediaFound"/>.
    /// </summary>
    public static string Describe(Uri url, IReadOnlyList<MediaExtractionAttempt> attempts) =>
        TryDescribeFailure(url, attempts) ?? NoMediaFound;

    /// <summary>
    /// As <see cref="Describe"/>, but <see langword="null"/> when there is nothing to explain — every
    /// extractor merely declined, which is not a failure worth warning about on its own.
    /// </summary>
    public static string? TryDescribeFailure(Uri url, IReadOnlyList<MediaExtractionAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(attempts);

        MediaExtractionAttempt? network =
            attempts.FirstOrDefault(a => a.Outcome == MediaExtractionOutcome.NetworkFailure);
        if (network is not null)
        {
            return string.Create(
                CultureInfo.CurrentCulture,
                $"Network error — couldn't reach {url.Host}. Check your connection or DNS. ({network.Reason})");
        }

        string[] reasons = attempts
            .Where(a => a.Outcome == MediaExtractionOutcome.Failed)
            .Take(MaxReasons)
            .Select(a => $"{a.ExtractorName}: {a.Reason}")
            .ToArray();

        return reasons.Length > 0
            ? "Couldn't extract media — " + string.Join(" · ", reasons)
            : null;
    }
}
