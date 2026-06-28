using JustDownload.Core.Downloading;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media.Streams;

/// <summary>
/// Default <see cref="ISeparateStreamDownloader"/> (TASK-039). Drives two concurrent
/// <see cref="ISegmentedDownloader"/> downloads — one per stream — each with its own progress, segment bar
/// and resume checkpoint. The two tasks are isolated: each is wrapped so its failure becomes a failed
/// <see cref="StreamOutcome"/> rather than faulting the other (AC2). Only an external cancellation stops
/// both. Both tasks are always awaited, so neither leaks if the other throws.
/// </summary>
internal sealed partial class SeparateStreamDownloader : ISeparateStreamDownloader
{
    private readonly ISegmentedDownloader _downloader;
    private readonly ILogger<SeparateStreamDownloader> _logger;

    public SeparateStreamDownloader(ISegmentedDownloader downloader, ILogger<SeparateStreamDownloader> logger)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(logger);
        _downloader = downloader;
        _logger = logger;
    }

    public async Task<SeparateStreamResult> DownloadAsync(
        StreamDownloadRequest video,
        StreamDownloadRequest audio,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(audio);

        // Start both concurrently; each isolates its own failure into a StreamOutcome.
        Task<StreamOutcome> videoTask = RunStreamAsync(video, cancellationToken);
        Task<StreamOutcome> audioTask = RunStreamAsync(audio, cancellationToken);

        StreamOutcome[] outcomes = await Task.WhenAll(videoTask, audioTask).ConfigureAwait(false);
        return new SeparateStreamResult(outcomes[0], outcomes[1]);
    }

    private async Task<StreamOutcome> RunStreamAsync(StreamDownloadRequest request, CancellationToken cancellationToken)
    {
        MediaStreamSpec spec = request.Spec;
        var downloadRequest = new DownloadRequest
        {
            Url = spec.Url,
            DestinationPath = spec.DestinationPath,
            Connections = spec.Connections,
            Headers = spec.Headers,
        };

        try
        {
            DownloadResult result = await _downloader.DownloadAsync(
                downloadRequest,
                request.Progress,
                request.Received,
                request.ConnectionProgress,
                connections: null,
                cancellationToken).ConfigureAwait(false);

            return new StreamOutcome
            {
                Role = spec.Role,
                DestinationPath = spec.DestinationPath,
                Succeeded = true,
                Result = result,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user cancelled both streams — propagate so the whole operation cancels.
            throw;
        }
        catch (Exception ex)
        {
            // Isolate this stream's failure: the other keeps running, and this stream's partial file +
            // resume checkpoint remain so it can be retried/resumed without re-fetching the sibling.
            LogStreamFailed(_logger, spec.Role, spec.Url, ex);
            return new StreamOutcome
            {
                Role = spec.Role,
                DestinationPath = spec.DestinationPath,
                Succeeded = false,
                Error = ex,
            };
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "{Role} stream {Url} failed; it remains resumable.")]
    private static partial void LogStreamFailed(ILogger logger, StreamRole role, Uri url, Exception exception);
}
