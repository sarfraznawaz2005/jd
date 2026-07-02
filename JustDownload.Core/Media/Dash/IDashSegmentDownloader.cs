namespace JustDownload.Core.Media.Dash;

/// <summary>
/// Downloads a DASH SegmentTemplate/SegmentList representation's segments into ordered local files
/// (TASK-102): re-fetches and re-parses the manifest identified by <paramref name="representationUri"/> — a
/// URI encoding the manifest location plus the chosen representation's id
/// (<see cref="MpdParser.TryParseRepresentationUri"/>) — resolves its init + media segment URLs
/// (<see cref="MpdParser.ResolveSegments"/>), and downloads them with bounded parallelism, mirroring
/// <see cref="Hls.IHlsDownloader"/>. Concatenation into one file is a separate step.
/// </summary>
public interface IDashSegmentDownloader
{
    /// <summary>
    /// Downloads every segment of the representation identified by <paramref name="representationUri"/> into
    /// <paramref name="workingDirectory"/>, returning the ordered segment files.
    /// </summary>
    /// <param name="representationUri">The manifest+representation-id identifier URI (a chosen video/audio variant).</param>
    /// <param name="workingDirectory">A directory to write the segment files into (created if absent).</param>
    /// <param name="headers">Extra request headers (cookies/referrer) to replay on every request.</param>
    /// <param name="progress">Optional sink for segment-count progress.</param>
    /// <param name="cancellationToken">Cancels the download promptly.</param>
    /// <exception cref="DashExtractionException">
    /// The manifest could not be fetched/parsed, or the representation's segments could not be resolved.
    /// </exception>
    Task<DashSegmentDownloadResult> DownloadAsync(
        Uri representationUri,
        string workingDirectory,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null,
        IProgress<DashSegmentProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Thrown when DASH segment extraction/download fails in a way the user should see (US-9, "honest extraction").</summary>
public sealed class DashExtractionException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public DashExtractionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner cause.</summary>
    public DashExtractionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
