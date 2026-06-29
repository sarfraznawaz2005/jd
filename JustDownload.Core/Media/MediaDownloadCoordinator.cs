using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Media.Hls;
using JustDownload.Core.Media.Streams;

namespace JustDownload.Core.Media;

/// <summary>
/// Default <see cref="IMediaDownloadCoordinator"/> (TASK-154). HLS: downloads the media playlist's segments
/// (<see cref="IHlsDownloader"/>) and concatenates them (<see cref="IHlsConcatenator"/>) — MPEG-TS,
/// ffprobe-valid. SeparateStreams/DASH: downloads the video and audio streams concurrently
/// (<see cref="ISeparateStreamDownloader"/>) and muxes them into one container by stream copy
/// (<see cref="IMediaMuxer"/>). Segment/byte progress is forwarded as a fraction.
/// </summary>
internal sealed class MediaDownloadCoordinator : IMediaDownloadCoordinator
{
    private readonly IHlsDownloader _hlsDownloader;
    private readonly IHlsConcatenator _hlsConcatenator;
    private readonly ISeparateStreamDownloader _separateStreamDownloader;
    private readonly IMediaMuxer _mediaMuxer;

    public MediaDownloadCoordinator(
        IHlsDownloader hlsDownloader,
        IHlsConcatenator hlsConcatenator,
        ISeparateStreamDownloader separateStreamDownloader,
        IMediaMuxer mediaMuxer)
    {
        ArgumentNullException.ThrowIfNull(hlsDownloader);
        ArgumentNullException.ThrowIfNull(hlsConcatenator);
        ArgumentNullException.ThrowIfNull(separateStreamDownloader);
        ArgumentNullException.ThrowIfNull(mediaMuxer);
        _hlsDownloader = hlsDownloader;
        _hlsConcatenator = hlsConcatenator;
        _separateStreamDownloader = separateStreamDownloader;
        _mediaMuxer = mediaMuxer;
    }

    public Task<MediaDownloadOutcome> DownloadAsync(
        MediaDownloadRequest request,
        IProgress<MediaDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Kind switch
        {
            MediaKind.Hls => DownloadHlsAsync(request, progress, cancellationToken),
            MediaKind.SeparateStreams or MediaKind.Dash =>
                DownloadSeparateStreamsAsync(request, progress, cancellationToken),
            _ => throw new NotSupportedException(
                $"Media kind {request.Kind} is not supported by the download engine."),
        };
    }

    private async Task<MediaDownloadOutcome> DownloadHlsAsync(
        MediaDownloadRequest request, IProgress<MediaDownloadProgress>? progress, CancellationToken cancellationToken)
    {
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

    private async Task<MediaDownloadOutcome> DownloadSeparateStreamsAsync(
        MediaDownloadRequest request, IProgress<MediaDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (request.AudioUrl is null)
        {
            throw new NotSupportedException("A separate-streams media download requires an audio stream URL.");
        }

        Directory.CreateDirectory(request.WorkingDirectory);
        string videoPath = Path.Combine(request.WorkingDirectory, "video.stream");
        string audioPath = Path.Combine(request.WorkingDirectory, "audio.stream");

        long videoBytes = 0;
        long audioBytes = 0;
        void Report()
        {
            // Sizes aren't known up front, so report a running byte total with an indeterminate fraction.
            progress?.Report(new MediaDownloadProgress(0, videoBytes + audioBytes));
        }

        var video = new StreamDownloadRequest
        {
            Spec = new MediaStreamSpec
            {
                Url = request.MediaUrl,
                Role = StreamRole.Video,
                DestinationPath = videoPath,
                Headers = request.Headers,
            },
            Progress = new Progress<long>(b => { videoBytes = b; Report(); }),
        };
        var audio = new StreamDownloadRequest
        {
            Spec = new MediaStreamSpec
            {
                Url = request.AudioUrl,
                Role = StreamRole.Audio,
                DestinationPath = audioPath,
                Headers = request.Headers,
            },
            Progress = new Progress<long>(b => { audioBytes = b; Report(); }),
        };

        SeparateStreamResult result = await _separateStreamDownloader
            .DownloadAsync(video, audio, cancellationToken).ConfigureAwait(false);
        if (!result.AllSucceeded)
        {
            throw result.Video.Error ?? result.Audio.Error
                ?? new InvalidOperationException("A media stream failed to download.");
        }

        await _mediaMuxer.MuxAsync(
            new MuxRequest
            {
                VideoPath = videoPath,
                AudioPath = audioPath,
                PreferredContainer = request.Container,
                OutputPath = request.OutputPath,
            },
            progress: null,
            cancellationToken).ConfigureAwait(false);

        long total = (result.Video.Result?.TotalBytes ?? 0) + (result.Audio.Result?.TotalBytes ?? 0);
        return new MediaDownloadOutcome(total);
    }
}
