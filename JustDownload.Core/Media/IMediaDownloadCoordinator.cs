using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Settings;

namespace JustDownload.Core.Media;

/// <summary>What the engine needs to download a chosen media variant to a file (TASK-154).</summary>
public sealed record MediaDownloadRequest
{
    /// <summary>The media path: currently <see cref="MediaKind.Hls"/> (others land in later increments).</summary>
    public required MediaKind Kind { get; init; }

    /// <summary>The media playlist/variant URL to download (the video stream for separate-streams).</summary>
    public required Uri MediaUrl { get; init; }

    /// <summary>The audio stream URL for a separate-streams download, or <see langword="null"/> (HLS).</summary>
    public Uri? AudioUrl { get; init; }

    /// <summary>The preferred output container when muxing separate streams. Default <see cref="MediaContainer.Mkv"/>.</summary>
    public MediaContainer Container { get; init; } = MediaContainer.Mkv;

    /// <summary>The final output file path.</summary>
    public required string OutputPath { get; init; }

    /// <summary>A scratch directory for intermediate segment files (created if absent, removed on success).</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Extra request headers (cookies/referrer) replayed on every media request.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];
}

/// <summary>
/// Which part of a media download is running (TASK-154). The local join/mux step follows the last fetched
/// byte and can take a while on a large file, so it is reported distinctly rather than left looking like a
/// transfer that stopped making progress.
/// </summary>
public enum MediaDownloadPhase
{
    /// <summary>Fetching segments or streams from the network.</summary>
    Downloading,

    /// <summary>Joining the fetched segments and muxing the streams into the output container.</summary>
    Combining,
}

/// <summary>Progress of a media download (TASK-154): a 0–1 fraction by segment count and the running byte total.</summary>
public readonly record struct MediaDownloadProgress(
    double Fraction,
    long DownloadedBytes,
    MediaDownloadPhase Phase = MediaDownloadPhase.Downloading);

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
