using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Lifecycle;

/// <summary>
/// Default <see cref="IBatchEnqueuer"/> (TASK-074). Expands the pasted text, filters to valid absolute
/// downloadable URLs (http/https/ftp/ftps), derives a file name from each URL, and enqueues them in order
/// through the manager. Invalid tokens are skipped (and logged) rather than failing the whole batch.
/// </summary>
internal sealed partial class BatchEnqueuer : IBatchEnqueuer
{
    private static readonly HashSet<string> SupportedSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "ftp", "ftps" };

    private readonly IDownloadManager _manager;
    private readonly ILogger<BatchEnqueuer> _logger;

    public BatchEnqueuer(IDownloadManager manager, ILogger<BatchEnqueuer> logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(logger);
        _manager = manager;
        _logger = logger;
    }

    public async Task<IReadOnlyList<long>> EnqueueAsync(
        BatchEnqueueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.DestinationDirectory);

        var ids = new List<long>();
        foreach (string candidate in BatchUrlExpander.Expand(request.Text))
        {
            if (!TryCreateUrl(candidate, out Uri? url))
            {
                LogSkipped(_logger, candidate);
                continue;
            }

            var enqueue = new EnqueueDownloadRequest
            {
                Url = url,
                DestinationDirectory = request.DestinationDirectory,
                FileName = DeriveFileName(url),
                Referrer = request.Referrer,
                MaxConnections = request.MaxConnections,
                SpeedLimit = request.SpeedLimit,
            };

            long id = await _manager.EnqueueAsync(enqueue, cancellationToken).ConfigureAwait(false);
            ids.Add(id);
        }

        LogEnqueued(_logger, ids.Count);
        return ids;
    }

    private static bool TryCreateUrl(string candidate, [NotNullWhen(true)] out Uri? url)
    {
        url = null;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? parsed) || !SupportedSchemes.Contains(parsed.Scheme))
        {
            return false;
        }

        url = parsed;
        return true;
    }

    private static string DeriveFileName(Uri url)
    {
        string name = Path.GetFileName(url.AbsolutePath);
        return string.IsNullOrEmpty(name) ? "download" : Uri.UnescapeDataString(name);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Batch enqueued {Count} downloads.")]
    private static partial void LogEnqueued(ILogger logger, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Skipped non-downloadable batch entry '{Entry}'.")]
    private static partial void LogSkipped(ILogger logger, string entry);
}
