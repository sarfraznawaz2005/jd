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
    /// Returns the first recognising extractor's <see cref="MediaSource"/>, or <see langword="null"/> if no
    /// extractor handles <paramref name="request"/> (the caller then treats it as a plain download or shows
    /// a "couldn't extract" message — TASK-036 AC2). One extractor throwing does not abort the chain.
    /// </summary>
    /// <param name="request">The candidate URL and its hints.</param>
    /// <param name="cancellationToken">Cancels the extraction.</param>
    Task<MediaSource?> ExtractAsync(MediaRequest request, CancellationToken cancellationToken = default);
}
