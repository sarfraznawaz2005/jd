namespace JustDownload.Core.Media.Extraction;

/// <summary>
/// The outcome of a whole extraction pass: the first recognising extractor's <see cref="Source"/> (or
/// <see langword="null"/> when none recognised the URL) plus one <see cref="MediaExtractionAttempt"/> per
/// extractor that was tried, in the order they ran. The attempts are what let a caller tell a DNS failure
/// apart from "nothing here is downloadable" instead of showing one generic message for both.
/// </summary>
public sealed record MediaExtractionResult
{
    /// <summary>An extraction that never ran (e.g. the caller skipped the registry for this URL).</summary>
    public static MediaExtractionResult None { get; } = new();

    /// <summary>The recognised media, or <see langword="null"/> when no extractor produced one.</summary>
    public MediaSource? Source { get; init; }

    /// <summary>Every extractor tried, in run order. Empty only when no extractor ran at all.</summary>
    public IReadOnlyList<MediaExtractionAttempt> Attempts { get; init; } = [];
}
