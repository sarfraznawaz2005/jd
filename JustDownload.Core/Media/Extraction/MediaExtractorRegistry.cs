using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media.Extraction;

/// <summary>
/// Default <see cref="IMediaExtractorRegistry"/> (TASK-036). Orders the DI-supplied extractors by ascending
/// <see cref="IMediaExtractor.Priority"/> once at construction, then on each request tries them in turn and
/// returns the first non-null result. A misbehaving extractor that throws is logged and skipped rather than
/// failing the whole extraction — but the failure is no longer swallowed: it is recorded as a
/// <see cref="MediaExtractionAttempt"/> on the result, so the caller can tell a DNS failure apart from a
/// genuine decline (CLAUDE.md §5, no silent failures). Cancellation propagates — it is not swallowed as an
/// extractor fault.
/// </summary>
internal sealed partial class MediaExtractorRegistry : IMediaExtractorRegistry
{
    private readonly ILogger<MediaExtractorRegistry> _logger;

    public MediaExtractorRegistry(IEnumerable<IMediaExtractor> extractors, ILogger<MediaExtractorRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(extractors);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        Extractors = extractors.OrderBy(e => e.Priority).ToArray();
    }

    public IReadOnlyList<IMediaExtractor> Extractors { get; }

    public async Task<MediaExtractionResult> ExtractAsync(
        MediaRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var attempts = new List<MediaExtractionAttempt>(Extractors.Count);

        foreach (IMediaExtractor extractor in Extractors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            MediaSource? source;
            try
            {
                source = await extractor.TryExtractAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (MediaExtractionFailedException ex)
            {
                // "Mine, but it failed" — the extractor supplied the reason itself. Log the attempt's own
                // redacted reason rather than the raw message: logs redact signed-URL query strings too (§5).
                MediaExtractionAttempt failure = MediaExtractionAttempt.Failed(extractor.Name, ex.Message);
                LogExtractorReportedFailure(_logger, extractor.Name, request.Url, failure.Reason ?? ex.Message);
                attempts.Add(failure);
                continue;
            }
            catch (Exception ex) when (IsNetworkFailure(ex))
            {
                // The host could not be reached at all — that says nothing about whether media exists there,
                // so it must never be reported to the user as "no media found".
                LogExtractorNetworkFailure(_logger, extractor.Name, request.Url, ex);
                attempts.Add(MediaExtractionAttempt.NetworkFailure(extractor.Name, Describe(ex)));
                continue;
            }
            catch (Exception ex)
            {
                // A single extractor failing is not fatal — record it and let the next one try.
                LogExtractorFailed(_logger, extractor.Name, request.Url, ex);
                attempts.Add(MediaExtractionAttempt.Failed(extractor.Name, Describe(ex)));
                continue;
            }

            if (source is not null)
            {
                attempts.Add(MediaExtractionAttempt.Accepted(extractor.Name));
                LogExtracted(_logger, extractor.Name, source.Kind, request.Url);
                return new MediaExtractionResult { Source = source, Attempts = attempts };
            }

            attempts.Add(MediaExtractionAttempt.Declined(extractor.Name));
        }

        LogNoMatch(_logger, request.Url);
        return new MediaExtractionResult { Attempts = attempts };
    }

    /// <summary>
    /// Transport-level failures, including the ones wrapped inside another exception (an
    /// <see cref="HttpRequestException"/> around a <see cref="SocketException"/> is how a DNS failure
    /// arrives). An <see cref="OperationCanceledException"/> only reaches here when the caller's own token
    /// was not cancelled, which means an extractor's internal timeout fired — also a connectivity problem.
    /// </summary>
    private static bool IsNetworkFailure(Exception exception) =>
        exception is HttpRequestException or SocketException or OperationCanceledException
        || (exception.InnerException is { } inner && IsNetworkFailure(inner));

    private static string Describe(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message}";

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Extractor {Extractor} recognised {Kind} at {Url}.")]
    private static partial void LogExtracted(ILogger logger, string extractor, MediaKind kind, Uri url);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "No extractor recognised {Url}.")]
    private static partial void LogNoMatch(ILogger logger, Uri url);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Extractor {Extractor} threw inspecting {Url}; skipping it.")]
    private static partial void LogExtractorFailed(ILogger logger, string extractor, Uri url, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Extractor {Extractor} could not extract {Url}: {Reason}")]
    private static partial void LogExtractorReportedFailure(ILogger logger, string extractor, Uri url, string reason);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Extractor {Extractor} could not reach {Url}.")]
    private static partial void LogExtractorNetworkFailure(ILogger logger, string extractor, Uri url, Exception exception);
}
