using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Lifecycle;

/// <summary>
/// Default <see cref="IDownloadRecovery"/> (TASK-029). Reads every persisted download and demotes any still
/// marked active — which can only happen after an unclean shutdown — to <see cref="DownloadStatus.Paused"/>
/// via the validated state machine, leaving the segment checkpoint untouched so a resume re-fetches nothing
/// already on disk. Runs once at startup before the queue is driven.
/// </summary>
internal sealed partial class DownloadRecoveryService : IDownloadRecovery
{
    private readonly IDownloadRepository _downloads;
    private readonly ILogger<DownloadRecoveryService> _logger;

    public DownloadRecoveryService(IDownloadRepository downloads, ILogger<DownloadRecoveryService> logger)
    {
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(logger);
        _downloads = downloads;
        _logger = logger;
    }

    public async Task<IReadOnlyList<long>> RecoverInterruptedAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Download> active = await _downloads
            .GetByStatusOrderedByPriorityAsync(DownloadStatusCodes.Active, cancellationToken)
            .ConfigureAwait(false);
        if (active.Count == 0)
        {
            return [];
        }

        // Active → Paused is the only recovery transition; assert the invariant once, then demote every
        // interrupted download in a single UPDATE rather than one per row (TASK-105).
        DownloadStateMachine.EnsureCanTransition(DownloadStatus.Active, DownloadStatus.Paused);
        await _downloads
            .MarkAllAsync(DownloadStatusCodes.Active, DownloadStatusCodes.Paused, cancellationToken)
            .ConfigureAwait(false);

        var recovered = active.Select(record => record.Id).ToList();
        LogRecovered(_logger, recovered.Count);
        return recovered;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Recovered {Count} interrupted download(s) from an unclean shutdown; marked resumable.")]
    private static partial void LogRecovered(ILogger logger, int count);
}
