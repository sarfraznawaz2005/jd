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
        IReadOnlyList<Download> all = await _downloads.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var recovered = new List<long>();
        foreach (Download record in all)
        {
            if (DownloadStatusCodes.Parse(record.Status) != DownloadStatus.Active)
            {
                continue;
            }

            DownloadStateMachine.EnsureCanTransition(DownloadStatus.Active, DownloadStatus.Paused);
            Download paused = record with { Status = DownloadStatusCodes.Paused };
            await _downloads.UpdateAsync(paused, cancellationToken).ConfigureAwait(false);
            recovered.Add(record.Id);
        }

        if (recovered.Count > 0)
        {
            LogRecovered(_logger, recovered.Count);
        }

        return recovered;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Recovered {Count} interrupted download(s) from an unclean shutdown; marked resumable.")]
    private static partial void LogRecovered(ILogger logger, int count);
}
