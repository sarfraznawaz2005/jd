using JustDownload.Core.Settings;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media.Streams;

/// <summary>
/// Default <see cref="IMediaMuxer"/> (TASK-041). Chooses the container with <see cref="MuxContainerSelector"/>
/// (MKV default, MP4 when codecs allow) and runs
/// <c>ffmpeg -y -i video -i audio -map 0:v? -map 1:a? -c copy [+faststart] output</c> through the
/// <see cref="IFfmpegRunner"/>. <c>-c copy</c> means a pure stream copy — no re-encode (AC2) — which is fast
/// and lossless. The explicit maps take the video from the first input and the audio from the second. On
/// failure the partial output is deleted and both inputs are untouched.
/// </summary>
internal sealed partial class MediaMuxer : IMediaMuxer
{
    private readonly IFfmpegRunner _runner;
    private readonly ILogger<MediaMuxer> _logger;

    public MediaMuxer(IFfmpegRunner runner, ILogger<MediaMuxer> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);
        _runner = runner;
        _logger = logger;
    }

    public async Task<MuxResult> MuxAsync(
        MuxRequest request,
        IProgress<FfmpegProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.VideoPath);
        ArgumentException.ThrowIfNullOrEmpty(request.AudioPath);

        if (!File.Exists(request.VideoPath))
        {
            throw new FileNotFoundException("Video stream to mux was not found.", request.VideoPath);
        }

        if (!File.Exists(request.AudioPath))
        {
            throw new FileNotFoundException("Audio stream to mux was not found.", request.AudioPath);
        }

        MediaContainer container = MuxContainerSelector.Select(
            request.PreferredContainer, request.VideoCodec, request.AudioCodec);
        string extension = container == MediaContainer.Mp4 ? ".mp4" : ".mkv";
        string output = request.OutputPath ?? Path.ChangeExtension(Path.GetFullPath(request.VideoPath), extension);

        var arguments = new List<string>
        {
            "-y",
            "-i", request.VideoPath,
            "-i", request.AudioPath,
            "-map", "0:v?",
            "-map", "1:a?",
            "-c", "copy",
        };

        if (container == MediaContainer.Mp4)
        {
            arguments.Add("-movflags");
            arguments.Add("+faststart");
        }

        arguments.Add(output);

        FfmpegRunResult result;
        try
        {
            result = await _runner.RunAsync(arguments, progress, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(output);
            throw;
        }

        if (!result.Succeeded)
        {
            TryDelete(output);
            throw new FfmpegException(
                $"Muxing '{request.VideoPath}' + '{request.AudioPath}' to {container} failed " +
                $"(exit {result.ExitCode}). {result.StandardError}");
        }

        LogMuxed(_logger, request.VideoPath, request.AudioPath, container, output);
        return new MuxResult(output, container);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Muxed {Video} + {Audio} → {Container} {Output}.")]
    private static partial void LogMuxed(ILogger logger, string video, string audio, MediaContainer container, string output);
}
