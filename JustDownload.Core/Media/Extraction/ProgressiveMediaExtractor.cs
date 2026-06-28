namespace JustDownload.Core.Media.Extraction;

/// <summary>
/// The generic catch-all extractor (TASK-036 AC1): recognises a directly-downloadable media file by its
/// extension or <c>Content-Type</c> and reports it as <see cref="MediaKind.Progressive"/>. It runs last
/// (highest <see cref="Priority"/>) so the specific HLS/DASH extractors win first; it performs no network
/// I/O. A URL it does not recognise yields <see langword="null"/>, leaving the registry to return no match.
/// </summary>
internal sealed class ProgressiveMediaExtractor : IMediaExtractor
{
    // Common progressive media containers. HLS/DASH manifests (.m3u8/.mpd) are deliberately excluded —
    // those belong to their dedicated extractors, which run earlier.
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".flv", ".wmv", ".mpg", ".mpeg", ".ts", ".3gp",
        ".m4a", ".mp3", ".aac", ".ogg", ".oga", ".opus", ".flac", ".wav", ".wma",
    };

    /// <summary>Runs after every specific extractor.</summary>
    public int Priority => int.MaxValue;

    public string Name => "progressive";

    public Task<MediaSource?> TryExtractAsync(
        MediaRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        MediaSource? result = IsProgressiveMedia(request)
            ? new MediaSource
            {
                ExtractorName = Name,
                Kind = MediaKind.Progressive,
                Url = request.Url,
                SuggestedFileName = DeriveFileName(request.Url),
            }
            : null;

        return Task.FromResult(result);
    }

    private static bool IsProgressiveMedia(MediaRequest request)
    {
        if (request.ContentType is { Length: > 0 } contentType &&
            (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
             contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        string extension = Path.GetExtension(request.Url.AbsolutePath);
        return MediaExtensions.Contains(extension);
    }

    private static string? DeriveFileName(Uri url)
    {
        string name = Path.GetFileName(url.AbsolutePath);
        return string.IsNullOrEmpty(name) ? null : Uri.UnescapeDataString(name);
    }
}
