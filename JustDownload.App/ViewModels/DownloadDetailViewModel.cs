using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustDownload.App.Formatting;
using JustDownload.App.Services;
using JustDownload.Core.Integrity;
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
    private readonly IChecksumVerifier _checksums;
    private readonly Dictionary<int, ConnectionRowViewModel> _connectionsById = [];
    private Avalonia.Threading.DispatcherTimer? _sampler;
    private long _latestSpeed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SaveToDisplay))]
    [NotifyPropertyChangedFor(nameof(UrlDisplay))]
    [NotifyPropertyChangedFor(nameof(CategoryDisplay))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifyChecksumCommand))]
    [NotifyPropertyChangedFor(nameof(ShowWaitingForConnections))]
    private DownloadRowViewModel? _selected;

    /// <summary>The user-entered MD5/SHA-256 hash to verify the completed file against (Options tab, TASK-132).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyChecksumCommand))]
    private string _expectedChecksum = string.Empty;

    /// <summary>The verification result shown to the user ("✓ Matches", "✗ Does not match", …).</summary>
    [ObservableProperty]
    private string _checksumStatus = string.Empty;

    [ObservableProperty]
    private string _totalSizeDisplay = "—";

    [ObservableProperty]
    private string _downloadedDisplay = "—";

    [ObservableProperty]
    private string _segmentsDisplay = "—";

    [ObservableProperty]
    private int _activeTabIndex;

    public DownloadDetailViewModel(
        IDownloadManager manager, IDownloadActions actions, IChecksumVerifier? checksums = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(actions);
        _manager = manager;
        _actions = actions;
        _checksums = checksums ?? new ChecksumVerifier();
        Segments = new SegmentVisualizationViewModel(BuildStreamSnapshots);
        Connections.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowWaitingForConnections));
        _manager.ProgressChanged += OnProgressChanged;
        _manager.StatusChanged += OnStatusChanged;
    }

    /// <summary>The recent speed history for the selected download's sparkline (TASK-137).</summary>
    public SpeedSamples SpeedHistory { get; } = new();

    /// <summary>Records the selected download's latest speed into the history. Called on a timer; public for tests.</summary>
    public void SampleNow()
    {
        if (Selected is not null)
        {
            SpeedHistory.Add(Interlocked.Read(ref _latestSpeed));
        }
    }

    /// <summary>Begins sampling the selected download's speed once a second for the sparkline (TASK-137).</summary>
    public void Start()
    {
        if (_sampler is not null)
        {
            return;
        }

        _sampler = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sampler.Tick += (_, _) => SampleNow();
        _sampler.Start();
    }

    /// <summary>The live segment/connection visualization shown on the Download tab (TASK-055).</summary>
    public SegmentVisualizationViewModel Segments { get; }

    /// <summary>The live per-connection rows shown on the Connections tab.</summary>
    public ObservableCollection<ConnectionRowViewModel> Connections { get; } = [];

    /// <summary>Whether a download is currently selected (drives the empty-state placeholder).</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>
    /// Whether the "Waiting for connections…" hint should show. Only while actively downloading with no
    /// connections reported yet — a terminal state (completed/failed/paused) also has zero connections (its
    /// tracker is cleared on completion) and must not be mistaken for "still waiting" (user-reported: a
    /// completed download's Download tab said "Waiting for connections…").
    /// </summary>
    public bool ShowWaitingForConnections => Selected?.IsDownloading == true && Connections.Count == 0;

    /// <summary>The destination folder (Options tab).</summary>
    public string SaveToDisplay =>
        Selected?.FilePath is { } path ? Path.GetDirectoryName(path) ?? path : "—";

    /// <summary>The source URL (Options tab).</summary>
    public string UrlDisplay => Selected?.Url ?? "—";

    /// <summary>The resolved category (Options tab).</summary>
    public string CategoryDisplay => Selected?.Category.ToString() ?? "—";

    /// <summary>Points the detail at a download (or clears it), refreshing all tabs.</summary>
    public void Select(DownloadRowViewModel? row)
    {
        Selected = row;
        ExpectedChecksum = string.Empty;
        ChecksumStatus = string.Empty;
        Interlocked.Exchange(ref _latestSpeed, 0);
        SpeedHistory.Clear(); // a new download starts with a fresh graph
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

        Interlocked.Exchange(ref _latestSpeed, (long)e.Progress.BytesPerSecond);
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

        if (e.Current != DownloadStatus.Active)
        {
            // ProgressChanged stops firing once a download leaves Active, so nothing else would reset the
            // last observed speed — without this the sparkline keeps sampling a stale non-zero value forever
            // after Complete/Failed/Paused (user-reported: the graph "still shows" after a download finishes).
            Interlocked.Exchange(ref _latestSpeed, 0);
        }

        Dispatcher.UIThread.Post(() =>
        {
            ResumeCommand.NotifyCanExecuteChanged();
            PauseCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            VerifyChecksumCommand.NotifyCanExecuteChanged();
            RefreshConnections();
            UpdateSegmentVisualization();
            OnPropertyChanged(nameof(ShowWaitingForConnections)); // Selected.IsDownloading just flipped
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

    /// <summary>
    /// Verifies the completed file against <see cref="ExpectedChecksum"/> (TASK-132) and shows the outcome.
    /// Enabled only for a completed download with a resolved file path and a non-empty expected hash.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanVerifyChecksum))]
    private async Task VerifyChecksumAsync(CancellationToken cancellationToken)
    {
        if (Selected?.FilePath is not { Length: > 0 } path)
        {
            return;
        }

        ChecksumStatus = "Verifying…";
        ChecksumResult result = await _checksums.VerifyAsync(path, ExpectedChecksum, cancellationToken);
        ChecksumStatus = result.Outcome switch
        {
            ChecksumOutcome.Match => "✓ Matches",
            ChecksumOutcome.Mismatch => "✗ Does not match",
            ChecksumOutcome.UnrecognizedHashFormat => "Unrecognized hash (expected MD5 or SHA-256)",
            ChecksumOutcome.FileNotFound => "File not found",
            _ => "—",
        };
    }

    private bool CanVerifyChecksum() =>
        Selected?.IsCompleted == true &&
        Selected.FilePath is { Length: > 0 } &&
        !string.IsNullOrWhiteSpace(ExpectedChecksum);

    public void Dispose()
    {
        _sampler?.Stop();
        _manager.ProgressChanged -= OnProgressChanged;
        _manager.StatusChanged -= OnStatusChanged;
        Segments.Dispose();
    }
}
