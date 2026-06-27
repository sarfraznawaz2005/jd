using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using JustDownload.App.Formatting;
using JustDownload.Core.Lifecycle;

namespace JustDownload.App.ViewModels;

/// <summary>
/// The live status-bar summary (TASK-049 AC2): active download count, combined speed, and total connections.
/// Subscribes to the <see cref="IDownloadManager"/> events, folds them into a pure <see cref="StatusAggregate"/>,
/// and publishes the result on the UI thread. Bound by the status bar in the main window.
/// </summary>
public sealed partial class StatusSummaryViewModel : ViewModelBase, IDisposable
{
    private readonly IDownloadManager _manager;
    private readonly StatusAggregate _aggregate = new();
    private readonly object _gate = new();

    [ObservableProperty]
    private int _activeCount;

    [ObservableProperty]
    private int _connections;

    [ObservableProperty]
    private string _totalSpeedDisplay = "—";

    public StatusSummaryViewModel(IDownloadManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
        _manager.StatusChanged += OnStatusChanged;
        _manager.ProgressChanged += OnProgressChanged;
    }

    private void OnStatusChanged(object? sender, DownloadStatusChangedEventArgs e)
    {
        lock (_gate)
        {
            if (e.Current == DownloadStatus.Active)
            {
                _aggregate.Activate(e.DownloadId);
            }
            else
            {
                _aggregate.Deactivate(e.DownloadId);
            }
        }

        Publish();
    }

    private void OnProgressChanged(object? sender, DownloadProgressChangedEventArgs e)
    {
        if (e.Progress.Status != DownloadStatus.Active)
        {
            return;
        }

        lock (_gate)
        {
            _aggregate.Update(e.DownloadId, e.Progress.BytesPerSecond, e.Progress.Connections);
        }

        Publish();
    }

    private void Publish()
    {
        int active;
        int connections;
        double speed;
        lock (_gate)
        {
            active = _aggregate.ActiveCount;
            connections = _aggregate.TotalConnections;
            speed = _aggregate.TotalBytesPerSecond;
        }

        // Bound properties must change on the UI thread; events arrive from download worker threads.
        Dispatcher.UIThread.Post(() =>
        {
            ActiveCount = active;
            Connections = connections;
            TotalSpeedDisplay = ByteFormatter.FormatSpeed(speed);
        });
    }

    public void Dispose()
    {
        _manager.StatusChanged -= OnStatusChanged;
        _manager.ProgressChanged -= OnProgressChanged;
    }
}
