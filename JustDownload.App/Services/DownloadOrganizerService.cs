using JustDownload.Core.Categorization;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Lifecycle;
using Microsoft.Extensions.Logging;

namespace JustDownload.App.Services;

/// <summary>
/// Moves a completed download into its category subfolder when the user has opted in
/// (<see cref="JustDownload.Core.Settings.AppSettings.OrganizeByCategory"/>, TASK-046). Before TASK-180,
/// <see cref="IDownloadOrganizer"/> was fully implemented and DI-registered but never invoked from anywhere
/// in the completion pipeline — the setting had no effect. Listens for terminal Completed transitions,
/// same pattern as <see cref="AutoExtractService"/>/<see cref="PostDownloadCommandService"/>, and — since
/// organizing actually moves the file — persists the new path back to the repository so "Open file"/"Open
/// folder" keep working afterward. Failures are logged, never thrown. Start it once at app startup; dispose
/// unsubscribes.
/// <para>
/// Known, accepted race (matches this codebase's existing best-effort tolerance for other
/// completion-triggered services, e.g. auto-extract vs. the on-completion command): if both auto-extract
/// and organize-by-category are enabled for the same archive, they run concurrently and may contend for the
/// same file. Worst case is a caught, logged failure of one of the two — never data loss or a crash.
/// </para>
/// </summary>
public sealed partial class DownloadOrganizerService : IDisposable
{
    private readonly IDownloadManager _manager;
    private readonly IDownloadRepository _repository;
    private readonly IDownloadOrganizer _organizer;
    private readonly ILogger<DownloadOrganizerService> _logger;
    private bool _disposed;

    public DownloadOrganizerService(
        IDownloadManager manager,
        IDownloadRepository repository,
        IDownloadOrganizer organizer,
        ILogger<DownloadOrganizerService> logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(organizer);
        ArgumentNullException.ThrowIfNull(logger);
        _manager = manager;
        _repository = repository;
        _organizer = organizer;
        _logger = logger;
    }

    /// <summary>Begins listening for completed downloads.</summary>
    public void Start() => _manager.StatusChanged += OnStatusChanged;

    private void OnStatusChanged(object? sender, DownloadStatusChangedEventArgs e)
    {
        if (e.Current == DownloadStatus.Completed)
        {
            _ = OrganizeAsync(e.DownloadId);
        }
    }

    private async Task OrganizeAsync(long id)
    {
        try
        {
            Download? record = await _repository.GetAsync(id).ConfigureAwait(false);
            if (record?.Directory is null || record.Filename is null)
            {
                return;
            }

            if (!Enum.TryParse(record.CategoryType, out FileCategory category))
            {
                return; // no resolved category on this record — nothing to organize by
            }

            string path = Path.Combine(record.Directory, record.Filename);
            string finalPath = await _organizer.OrganizeAsync(path, category).ConfigureAwait(false);
            if (string.Equals(finalPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return; // toggle off, or already in place — IDownloadOrganizer didn't move anything
            }

            Download updated = record with
            {
                Directory = Path.GetDirectoryName(finalPath),
                Filename = Path.GetFileName(finalPath),
            };
            await _repository.UpdateAsync(updated).ConfigureAwait(false);
            LogOrganized(_logger, record.Filename, finalPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogOrganizeFailed(_logger, id, ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _manager.StatusChanged -= OnStatusChanged;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Organized {Original} to {Destination}.")]
    private static partial void LogOrganized(ILogger logger, string original, string destination);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Organize-by-category failed for download {Id}.")]
    private static partial void LogOrganizeFailed(ILogger logger, long id, Exception exception);
}
