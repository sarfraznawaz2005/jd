namespace JustDownload.Core.Media.Extraction;

/// <summary>
/// Thrown by an <see cref="IMediaExtractor"/> that recognised the URL as its own but could not extract
/// media from it, carrying the reason the user needs to see (e.g. yt-dlp's "Sign in to confirm you're not
/// a bot"). This is how an extractor says "mine, but it failed, and here is why" — returning
/// <see langword="null"/> still means the plain, cheap "not mine" decline. The registry turns it into a
/// <see cref="MediaExtractionOutcome.Failed"/> attempt and carries on down the chain, so a failing
/// extractor never aborts extraction.
/// </summary>
public sealed class MediaExtractionFailedException : Exception
{
    public MediaExtractionFailedException()
        : base("Media extraction failed.")
    {
    }

    public MediaExtractionFailedException(string message)
        : base(message)
    {
    }

    public MediaExtractionFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
