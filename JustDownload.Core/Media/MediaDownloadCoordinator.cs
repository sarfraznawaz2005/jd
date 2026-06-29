using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Media.Hls;

namespace JustDownload.Core.Media;

/// <summary>
/// Default <see cref="IMediaDownloadCoordinator"/> (TASK-154). HLS: downloads the media playlist's segments
/// (<see cref="IHlsDownloader"/>) into the working directory and concatenates them into the output file
/// (<see cref="IHlsConcatenator"/>) — MPEG-TS, ffprobe-valid. Segment-count progress is forwarded as a
/// fraction. DASH/separate-stream + mux arrive in later increments.
/// </summary>
internal sealed class MediaDownloadCoordinator : IMediaDownloadCoordinator
{
    private readonly IHlsDownloader _hlsDownloader;
    private readonly IHlsConcatenator _hlsConcatenator;

    public MediaDownloadCoordinator(IHlsDownloader hlsDownloader, IHlsConcatenator hlsConcatenator)
    {
        ArgumentNullException.ThrowIfNull(hlsDownloader);
        ArgumentNullException.ThrowIfNull(hlsConcatenator);
        _hlsDownloader = hlsDownloader;
        _hlsConcatenator = hlsConcatenator;
    }

    public async Task<MediaDownloadOutcome> DownloadAsync(
        MediaDownloadRequest request,
        IProgress<MediaDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Kind != MediaKind.Hls)
        {
            throw new NotSupportedException(
                $"Media kind {request.Kind} is not yet supported by the download engine.");
        }

        Directory.CreateDirectory(request.WorkingDirectory);

        IProgress<HlsProgress>? hlsProgress = progress is null
            ? null
            : new Progress<HlsProgress>(p => progress.Report(new MediaDownloadProgress(p.Fraction, p.DownloadedBytes)));

        HlsDownloadResult download = await _hlsDownloader
            .DownloadAsync(request.MediaUrl, request.WorkingDirectory, request.Headers, hlsProgress, cancellationToken)
            .ConfigureAwait(false);

        await _hlsConcatenator
            .ConcatenateAsync(download.SegmentFiles, request.OutputPath, progress: null, cancellationToken)
            .ConfigureAwait(false);

        return new MediaDownloadOutcome(download.TotalBytes);
    }
}
