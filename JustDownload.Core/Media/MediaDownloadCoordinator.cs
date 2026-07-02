using JustDownload.Core.Media.Dash;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Media.Hls;
using JustDownload.Core.Media.Streams;

namespace JustDownload.Core.Media;

/// <summary>
/// Default <see cref="IMediaDownloadCoordinator"/> (TASK-154). HLS: downloads the media playlist's segments
/// (<see cref="IHlsDownloader"/>) and concatenates them (<see cref="IHlsConcatenator"/>) — MPEG-TS,
/// ffprobe-valid. SeparateStreams: downloads the video and audio streams concurrently
/// (<see cref="ISeparateStreamDownloader"/>) and muxes them into one container by stream copy
/// (<see cref="IMediaMuxer"/>). Dash (TASK-102): downloads each stream's SegmentTemplate/SegmentList segments
/// (<see cref="IDashSegmentDownloader"/>), concatenates them — reusing <see cref="IHlsConcatenator"/>, which
/// is a plain ordered byte-append with no HLS-specific logic — into <c>video.stream</c>/<c>audio.stream</c>,
/// then muxes exactly like SeparateStreams. Segment/byte progress is forwarded as a fraction.
/// </summary>
internal sealed class MediaDownloadCoordinator : IMediaDownloadCoordinator
{
    private readonly IHlsDownloader _hlsDownloader;
    private readonly IHlsConcatenator _hlsConcatenator;
    private readonly ISeparateStreamDownloader _separateStreamDownloader;
    private readonly IDashSegmentDownloader _dashSegmentDownloader;
    private readonly IMediaMuxer _mediaMuxer;

    public MediaDownloadCoordinator(
        IHlsDownloader hlsDownloader,
        IHlsConcatenator hlsConcatenator,
        ISeparateStreamDownloader separateStreamDownloader,
        IDashSegmentDownloader dashSegmentDownloader,
        IMediaMuxer mediaMuxer)
    {
        ArgumentNullException.ThrowIfNull(hlsDownloader);
        ArgumentNullException.ThrowIfNull(hlsConcatenator);
        ArgumentNullException.ThrowIfNull(separateStreamDownloader);
        ArgumentNullException.ThrowIfNull(dashSegmentDownloader);
        ArgumentNullException.ThrowIfNull(mediaMuxer);
        _hlsDownloader = hlsDownloader;
        _hlsConcatenator = hlsConcatenator;
        _separateStreamDownloader = separateStreamDownloader;
        _dashSegmentDownloader = dashSegmentDownloader;
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
            MediaKind.SeparateStreams => DownloadSeparateStreamsAsync(request, progress, cancellationToken),
            MediaKind.Dash => DownloadDashAsync(request, progress, cancellationToken),
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

    /// <summary>
    /// Downloads a DASH SegmentTemplate/SegmentList representation (TASK-102): each stream's segments
    /// (init + media) are fetched by <see cref="IDashSegmentDownloader"/> and concatenated in order into
    /// <c>video.stream</c>/<c>audio.stream</c> — a concatenated init segment followed by its media fragments
    /// is itself a valid, playable stream — then muxed exactly like <see cref="DownloadSeparateStreamsAsync"/>.
    /// When the manifest has no separate audio representation the concatenated video stream is the output.
    /// </summary>
    private async Task<MediaDownloadOutcome> DownloadDashAsync(
        MediaDownloadRequest request, IProgress<MediaDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.WorkingDirectory);

        long videoBytes = 0;
        long audioBytes = 0;
        void Report()
        {
            // Sizes aren't known up front, so report a running byte total with an indeterminate fraction.
            progress?.Report(new MediaDownloadProgress(0, videoBytes + audioBytes));
        }

        DashSegmentDownloadResult videoSegments = await _dashSegmentDownloader.DownloadAsync(
            request.MediaUrl,
            Path.Combine(request.WorkingDirectory, "video-segments"),
            request.Headers,
            new Progress<DashSegmentProgress>(p => { videoBytes = p.DownloadedBytes; Report(); }),
            cancellationToken).ConfigureAwait(false);

        string videoStreamPath = Path.Combine(request.WorkingDirectory, "video.stream");
        await _hlsConcatenator.ConcatenateAsync(
            videoSegments.SegmentFiles, videoStreamPath, progress: null, cancellationToken).ConfigureAwait(false);

        if (request.AudioUrl is null)
        {
            // No separate audio representation — the concatenated video segments are already the output.
            File.Move(videoStreamPath, request.OutputPath, overwrite: true);
            return new MediaDownloadOutcome(videoSegments.TotalBytes);
        }

        DashSegmentDownloadResult audioSegments = await _dashSegmentDownloader.DownloadAsync(
            request.AudioUrl,
            Path.Combine(request.WorkingDirectory, "audio-segments"),
            request.Headers,
            new Progress<DashSegmentProgress>(p => { audioBytes = p.DownloadedBytes; Report(); }),
            cancellationToken).ConfigureAwait(false);

        string audioStreamPath = Path.Combine(request.WorkingDirectory, "audio.stream");
        await _hlsConcatenator.ConcatenateAsync(
            audioSegments.SegmentFiles, audioStreamPath, progress: null, cancellationToken).ConfigureAwait(false);

        await _mediaMuxer.MuxAsync(
            new MuxRequest
            {
                VideoPath = videoStreamPath,
                AudioPath = audioStreamPath,
                PreferredContainer = request.Container,
                OutputPath = request.OutputPath,
            },
            progress: null,
            cancellationToken).ConfigureAwait(false);

        return new MediaDownloadOutcome(videoSegments.TotalBytes + audioSegments.TotalBytes);
    }
}
