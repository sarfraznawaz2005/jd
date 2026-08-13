namespace JustDownload.Core.Media.Extraction;

/// <summary>
/// The entry point to media extraction (TASK-036): tries every registered <see cref="IMediaExtractor"/> in
/// priority order and returns the first match, or <see langword="null"/> when nothing recognises the URL.
/// The engine asks the registry "is this media, and what kind?" without knowing which extractors exist.
/// </summary>
public interface IMediaExtractorRegistry
{
    /// <summary>The registered extractors, in the order they are tried (ascending priority).</summary>
    IReadOnlyList<IMediaExtractor> Extractors { get; }

    /// <summary>
    /// Returns the first recognising extractor's <see cref="MediaSource"/> in
    /// <see cref="MediaExtractionResult.Source"/>, or <see langword="null"/> there if no extractor handles
    /// <paramref name="request"/> (the caller then treats it as a plain download or explains the failure —
    /// TASK-036 AC2). One extractor throwing does not abort the chain; every extractor's outcome is
    /// reported in <see cref="MediaExtractionResult.Attempts"/> so the caller can say <em>why</em> nothing
    /// was found instead of guessing.
    /// </summary>
    /// <param name="request">The candidate URL and its hints.</param>
    /// <param name="cancellationToken">Cancels the extraction.</param>
    Task<MediaExtractionResult> ExtractAsync(MediaRequest request, CancellationToken cancellationToken = default);
}
