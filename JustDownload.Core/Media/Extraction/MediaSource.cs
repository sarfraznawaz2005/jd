namespace JustDownload.Core.Media.Extraction;

/// <summary>
/// The result of successfully recognising media at a URL (TASK-036): which extractor matched, the
/// <see cref="MediaKind"/>, the resolved media <see cref="Url"/> (after any redirect the extractor
/// followed), a suggested output file name, and — for adaptive media — the selectable
/// <see cref="Variants"/> so the quality selector can pick one. A <see langword="null"/> result from an
/// extractor means "not mine"; a <see langword="null"/> result from the registry means no extractor
/// recognised the URL (the engine then degrades gracefully — TASK-036 AC2).
/// </summary>
public sealed record MediaSource
{
    /// <summary>The <see cref="IMediaExtractor.Name"/> of the extractor that produced this result.</summary>
    public required string ExtractorName { get; init; }

    /// <summary>The kind of media recognised.</summary>
    public required MediaKind Kind { get; init; }

    /// <summary>The resolved media URL (the playlist/manifest/file to download).</summary>
    public required Uri Url { get; init; }

    /// <summary>A suggested output file name (without an enforced extension), or <see langword="null"/>.</summary>
    public string? SuggestedFileName { get; init; }

    /// <summary>
    /// The selectable quality variants for adaptive media (HLS master / DASH), highest-first is not
    /// required — the quality selector orders them. Empty for progressive media or a single media playlist.
    /// </summary>
    public IReadOnlyList<VideoVariant> Variants { get; init; } = [];

    /// <summary>
    /// The selectable audio renditions for separate-streams media (DASH / HLS alternate audio). Empty when
    /// audio is muxed into the video stream or the media is progressive.
    /// </summary>
    public IReadOnlyList<AudioVariant> AudioVariants { get; init; } = [];
}
