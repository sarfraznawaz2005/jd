using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media.Extraction;

/// <summary>
/// Default <see cref="IMediaExtractorRegistry"/> (TASK-036). Orders the DI-supplied extractors by ascending
/// <see cref="IMediaExtractor.Priority"/> once at construction, then on each request tries them in turn and
/// returns the first non-null result. A misbehaving extractor that throws is logged and skipped rather than
/// failing the whole extraction (CLAUDE.md §1 "no silent failures", but one bad plugin must not break the
/// chain). Cancellation propagates — it is not swallowed as an extractor fault.
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

    public async Task<MediaSource?> ExtractAsync(
        MediaRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        foreach (IMediaExtractor extractor in Extractors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            MediaSource? source;
            try
            {
                source = await extractor.TryExtractAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A single extractor failing is not fatal — log it and let the next one try.
                LogExtractorFailed(_logger, extractor.Name, request.Url, ex);
                continue;
            }

            if (source is not null)
            {
                LogExtracted(_logger, extractor.Name, source.Kind, request.Url);
                return source;
            }
        }

        LogNoMatch(_logger, request.Url);
        return null;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Extractor {Extractor} recognised {Kind} at {Url}.")]
    private static partial void LogExtracted(ILogger logger, string extractor, MediaKind kind, Uri url);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "No extractor recognised {Url}.")]
    private static partial void LogNoMatch(ILogger logger, Uri url);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Extractor {Extractor} threw inspecting {Url}; skipping it.")]
    private static partial void LogExtractorFailed(ILogger logger, string extractor, Uri url, Exception exception);
}
