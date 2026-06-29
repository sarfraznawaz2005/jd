using JustDownload.Core.Media.Extraction;

namespace JustDownload.Core.Media;

/// <summary>What the engine needs to download a chosen media variant to a file (TASK-154).</summary>
public sealed record MediaDownloadRequest
{
    /// <summary>The media path: currently <see cref="MediaKind.Hls"/> (others land in later increments).</summary>
    public required MediaKind Kind { get; init; }

    /// <summary>The media playlist/variant URL to download.</summary>
    public required Uri MediaUrl { get; init; }

    /// <summary>The final output file path.</summary>
    public required string OutputPath { get; init; }

    /// <summary>A scratch directory for intermediate segment files (created if absent, removed on success).</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Extra request headers (cookies/referrer) replayed on every media request.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];
}

/// <summary>Progress of a media download (TASK-154): a 0–1 fraction by segment count and the running byte total.</summary>
public readonly record struct MediaDownloadProgress(double Fraction, long DownloadedBytes);

/// <summary>The outcome of a media download (TASK-154): the total bytes written to the output file.</summary>
public sealed record MediaDownloadOutcome(long TotalBytes);

/// <summary>
/// Orchestrates downloading a chosen media variant to a single output file (TASK-154): for HLS it downloads
/// the playlist's segments and concatenates them; later increments add DASH/separate-stream + mux. The
/// lifecycle (status/persistence/progress surfacing) stays in the download manager — this just produces the file.
/// </summary>
public interface IMediaDownloadCoordinator
{
    Task<MediaDownloadOutcome> DownloadAsync(
        MediaDownloadRequest request,
        IProgress<MediaDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
