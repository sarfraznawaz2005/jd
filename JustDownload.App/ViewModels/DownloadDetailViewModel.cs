using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustDownload.App.Formatting;
using JustDownload.App.Services;
using JustDownload.Core.Lifecycle;

namespace JustDownload.App.ViewModels;

/// <summary>
/// The per-download detail view (TASK-054, US-15c): the Download/Options/Connections tabs for the currently
/// selected download, with per-item Pause/Resume/Cancel. It tracks the selected <see cref="DownloadRowViewModel"/>
/// (which already updates live from the manager's events) for identity and status, and folds the manager's
/// numeric progress plus per-connection stats into the tab content on each tick. The same view-model backs
/// both the inline panel and the detached window, so "detach" is just hosting it in a separate window.
/// </summary>
public sealed partial class DownloadDetailViewModel : ViewModelBase, IDisposable
{
    private readonly IDownloadManager _manager;
    private readonly IDownloadActions _actions;
    private readonly Dictionary<int, ConnectionRowViewModel> _connectionsById = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SaveToDisplay))]
    [NotifyPropertyChangedFor(nameof(UrlDisplay))]
    [NotifyPropertyChangedFor(nameof(CategoryDisplay))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(DetachCommand))]
    private DownloadRowViewModel? _selected;

    [ObservableProperty]
    private string _totalSizeDisplay = "—";

    [ObservableProperty]
    private string _downloadedDisplay = "—";

    [ObservableProperty]
    private string _segmentsDisplay = "—";

    [ObservableProperty]
    private int _activeTabIndex;

    public DownloadDetailViewModel(IDownloadManager manager, IDownloadActions actions)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(actions);
        _manager = manager;
        _actions = actions;
        Segments = new SegmentVisualizationViewModel(BuildStreamSnapshots);
        _manager.ProgressChanged += OnProgressChanged;
        _manager.StatusChanged += OnStatusChanged;
    }

    /// <summary>The live segment/connection visualization shown on the Download tab (TASK-055).</summary>
    public SegmentVisualizationViewModel Segments { get; }

    /// <summary>The live per-connection rows shown on the Connections tab.</summary>
    public ObservableCollection<ConnectionRowViewModel> Connections { get; } = [];

    /// <summary>Whether a download is currently selected (drives the empty-state placeholder).</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>The destination folder (Options tab).</summary>
    public string SaveToDisplay =>
        Selected?.FilePath is { } path ? Path.GetDirectoryName(path) ?? path : "—";

    /// <summary>The source URL (Options tab).</summary>
    public string UrlDisplay => Selected?.Url ?? "—";

    /// <summary>The resolved category (Options tab).</summary>
    public string CategoryDisplay => Selected?.Category.ToString() ?? "—";

    /// <summary>Raised when the user detaches the detail into its own window.</summary>
    public event EventHandler<DownloadRowViewModel>? DetachRequested;

    /// <summary>Points the detail at a download (or clears it), refreshing all tabs.</summary>
    public void Select(DownloadRowViewModel? row)
    {
        Selected = row;
        Connections.Clear();
        _connectionsById.Clear();
        RefreshStats();
        RefreshConnections();
        UpdateSegmentVisualization();
    }

    /// <summary>One stream per download today ("File"); muxed media will supply one snapshot per stream.</summary>
    private IReadOnlyList<StreamSnapshot> BuildStreamSnapshots() =>
        Selected is { } row ? [new StreamSnapshot("File", _manager.GetConnections(row.Id))] : [];

    /// <summary>Runs the capped-rate segment repaint only while the selected download is actively transferring.</summary>
    private void UpdateSegmentVisualization()
    {
        if (Selected?.IsDownloading == true)
        {
            Segments.Start();
        }
        else
        {
            Segments.Stop();
        }
    }

    private void OnProgressChanged(object? sender, DownloadProgressChangedEventArgs e)
    {
        if (Selected is null || e.DownloadId != Selected.Id)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            RefreshStats();
            RefreshConnections();
        });
    }

    private void OnStatusChanged(object? sender, DownloadStatusChangedEventArgs e)
    {
        if (Selected is null || e.DownloadId != Selected.Id)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            ResumeCommand.NotifyCanExecuteChanged();
            PauseCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            RefreshConnections();
            UpdateSegmentVisualization();
        });
    }

    private void RefreshStats()
    {
        if (Selected is null)
        {
            TotalSizeDisplay = "—";
            DownloadedDisplay = "—";
            SegmentsDisplay = "—";
            return;
        }

        DownloadProgress? progress = _manager.GetProgress(Selected.Id);
        TotalSizeDisplay = Selected.TotalBytes is > 0 ? ByteFormatter.FormatSize(Selected.TotalBytes.Value) : "—";
        DownloadedDisplay = progress is not null ? ByteFormatter.FormatSize(progress.DownloadedBytes) : "—";
        SegmentsDisplay = progress is { Connections: > 0 }
            ? progress.Connections.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "—";
    }

    private void RefreshConnections()
    {
        if (Selected is null)
        {
            Connections.Clear();
            _connectionsById.Clear();
            return;
        }

        IReadOnlyList<ConnectionStat> stats = _manager.GetConnections(Selected.Id);

        // Update existing rows in place and add new ones, so the list does not flicker between ticks.
        var seen = new HashSet<int>(stats.Count);
        foreach (ConnectionStat stat in stats)
        {
            seen.Add(stat.ConnectionId);
            if (_connectionsById.TryGetValue(stat.ConnectionId, out ConnectionRowViewModel? row))
            {
                row.Apply(stat);
            }
            else
            {
                var created = new ConnectionRowViewModel(stat);
                _connectionsById[stat.ConnectionId] = created;
                Connections.Add(created);
            }
        }

        // Drop rows for connections no longer reported (e.g. after the download stops).
        for (int i = Connections.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Connections[i].ConnectionId))
            {
                _connectionsById.Remove(Connections[i].ConnectionId);
                Connections.RemoveAt(i);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume() => _actions.Start(Selected!.Id);

    private bool CanResume() => Selected?.CanResume == true;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause() => _actions.Pause(Selected!.Id);

    private bool CanPause() => Selected?.CanPause == true;

    /// <summary>
    /// Cancels the active transfer. In this engine the only "halt but keep resumable" state is Paused, so a
    /// cancel stops the connections and leaves the partial file resumable (it does not delete the download —
    /// that is the list's Remove action).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Cancel() => _actions.Pause(Selected!.Id);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Detach() => DetachRequested?.Invoke(this, Selected!);

    public void Dispose()
    {
        _manager.ProgressChanged -= OnProgressChanged;
        _manager.StatusChanged -= OnStatusChanged;
        Segments.Dispose();
    }
}
