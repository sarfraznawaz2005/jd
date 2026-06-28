using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Lifecycle;

namespace JustDownload.App.Services;

/// <summary>
/// Raises a user notification when a download completes or fails (TASK-061 AC0). It listens to the
/// <see cref="IDownloadManager"/>'s status changes and, on a terminal transition, looks up the file name and
/// shows a success/error toast through the <see cref="INotificationService"/>. Start it once at app startup;
/// dispose unsubscribes.
/// </summary>
public sealed class DownloadNotifier : IDisposable
{
    private readonly IDownloadManager _manager;
    private readonly IDownloadRepository _repository;
    private readonly INotificationService _notifications;
    private bool _disposed;

    public DownloadNotifier(
        IDownloadManager manager, IDownloadRepository repository, INotificationService notifications)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(notifications);
        _manager = manager;
        _repository = repository;
        _notifications = notifications;
    }

    /// <summary>Begins listening for completion/error transitions.</summary>
    public void Start() => _manager.StatusChanged += OnStatusChanged;

    private void OnStatusChanged(object? sender, DownloadStatusChangedEventArgs e)
    {
        if (e.Current is DownloadStatus.Completed or DownloadStatus.Failed)
        {
            _ = NotifyAsync(e.DownloadId, e.Current);
        }
    }

    private async Task NotifyAsync(long id, DownloadStatus status)
    {
        string name;
        try
        {
            name = (await _repository.GetAsync(id).ConfigureAwait(false))?.Filename ?? "Download";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            name = "Download";
        }

        AppNotification notification = status == DownloadStatus.Completed
            ? new AppNotification("Download complete", $"{name} finished downloading.", AppNotificationKind.Success)
            : new AppNotification("Download failed", $"{name} could not be downloaded.", AppNotificationKind.Error);

        _notifications.Notify(notification);
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
}
