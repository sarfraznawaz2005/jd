namespace JustDownload.Core.Media.Extraction;

/// <summary>
/// What happened when the registry tried one <see cref="IMediaExtractor"/>. Constructed only through the
/// factory methods below, so the pairing of <see cref="Outcome"/> and <see cref="Reason"/> is guaranteed:
/// <see cref="MediaExtractionOutcome.Accepted"/>/<see cref="MediaExtractionOutcome.Declined"/> never carry
/// a reason, and the two failure outcomes always do (already redacted for display, CLAUDE.md §5).
/// </summary>
public sealed record MediaExtractionAttempt
{
    private MediaExtractionAttempt(string extractorName, MediaExtractionOutcome outcome, string? reason)
    {
        ExtractorName = extractorName;
        Outcome = outcome;
        Reason = reason;
    }

    /// <summary>The <see cref="IMediaExtractor.Name"/> of the extractor that was tried.</summary>
    public string ExtractorName { get; }

    /// <summary>What the extractor did with the request.</summary>
    public MediaExtractionOutcome Outcome { get; }

    /// <summary>
    /// A short, user-safe explanation — non-<see langword="null"/> exactly when <see cref="Outcome"/> is
    /// <see cref="MediaExtractionOutcome.Failed"/> or <see cref="MediaExtractionOutcome.NetworkFailure"/>.
    /// </summary>
    public string? Reason { get; }

    public static MediaExtractionAttempt Accepted(string extractorName) =>
        new(Named(extractorName), MediaExtractionOutcome.Accepted, reason: null);

    public static MediaExtractionAttempt Declined(string extractorName) =>
        new(Named(extractorName), MediaExtractionOutcome.Declined, reason: null);

    public static MediaExtractionAttempt Failed(string extractorName, string? reason) =>
        new(Named(extractorName), MediaExtractionOutcome.Failed, ExtractionReasonSanitizer.Sanitize(reason));

    public static MediaExtractionAttempt NetworkFailure(string extractorName, string? reason) =>
        new(Named(extractorName), MediaExtractionOutcome.NetworkFailure, ExtractionReasonSanitizer.Sanitize(reason));

    private static string Named(string extractorName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractorName);
        return extractorName;
    }
}
