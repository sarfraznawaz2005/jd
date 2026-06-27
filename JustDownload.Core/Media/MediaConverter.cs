using JustDownload.Core.Settings;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media;

/// <summary>
/// Default <see cref="IMediaConverter"/> (TASK-042). Runs <c>ffmpeg -i input -c copy output</c> (adding
/// <c>+faststart</c> for mp4) through the <see cref="IFfmpegRunner"/>. Stream copy means no quality loss
/// and near-instant conversion. If ffmpeg fails, the partial output is deleted and the original is kept,
/// so a failed conversion never destroys the download.
/// </summary>
internal sealed partial class MediaConverter : IMediaConverter
{
    private readonly IFfmpegRunner _runner;
    private readonly ILogger<MediaConverter> _logger;

    public MediaConverter(IFfmpegRunner runner, ILogger<MediaConverter> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);
        _runner = runner;
        _logger = logger;
    }

    public async Task<string> RemuxAsync(
        string inputPath,
        MediaContainer container,
        string? outputPath = null,
        IProgress<FfmpegProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Media file to remux was not found.", inputPath);
        }

        string extension = container == MediaContainer.Mp4 ? ".mp4" : ".mkv";
        string output = outputPath ?? Path.ChangeExtension(Path.GetFullPath(inputPath), extension);

        var arguments = new List<string> { "-y", "-i", inputPath, "-c", "copy" };
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
            // Remove the partial output; the source is untouched and remains valid.
            TryDelete(output);
            throw new FfmpegException(
                $"Remux of '{inputPath}' to {container} failed (exit {result.ExitCode}). {result.StandardError}");
        }

        LogRemuxed(_logger, inputPath, output);
        return output;
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Remuxed {Input} → {Output}.")]
    private static partial void LogRemuxed(ILogger logger, string input, string output);
}
