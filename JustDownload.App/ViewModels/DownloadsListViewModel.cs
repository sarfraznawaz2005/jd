using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustDownload.App.Services;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Categorization;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Lifecycle;

namespace JustDownload.App.ViewModels;

/// <summary>
/// The downloads list (TASK-051): loads the persisted downloads, keeps each row live from the manager's
/// status/progress events, and exposes the per-row context-menu commands. It depends only on interfaces
/// (§6) — the repository for the initial load, the manager for live events, and an
/// <see cref="IDownloadActions"/>/clipboard/file-revealer for the menu actions — so it is testable headless.
/// Column sorting/reordering/hiding is handled by the <c>DataGrid</c> in the view; this view-model owns only
/// the data and behaviour.
/// </summary>
public sealed partial class DownloadsListViewModel : ViewModelBase, IDisposable
{
    private readonly IDownloadRepository _repository;
    private readonly IDownloadManager _manager;
    private readonly IDownloadActions _actions;
    private readonly IClipboardService _clipboard;
    private readonly IFileRevealer _fileRevealer;
    private readonly IFileCategorizer _categorizer;
    private readonly IClock _clock;
    private readonly Dictionary<long, DownloadRowViewModel> _byId = new();

    // The full set (newest first) is the source of truth; Downloads is the filtered view bound to the grid.
    private readonly List<DownloadRowViewModel> _allRows = new();
    private DownloadFilter _filter = DownloadFilter.All;

    // Which rows are currently in the visible Downloads collection — an O(1) membership check so a status
    // change that doesn't move a row in/out of the filter does no list scan (TASK-108).
    private readonly HashSet<long> _visibleIds = new();

    // Running counts maintained incrementally so a status change adjusts +1/-1 instead of rescanning the full
    // set (TASK-108). Category totals only change on add/remove; the completed/incomplete split also on status.
    private readonly Dictionary<FileCategory, int> _categoryCounts = new();
    private int _completedCount;
    private int _incompleteCount;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _loadError;

    /// <summary>Free-text search over filename and URL (TASK-134); empty matches everything.</summary>
    [ObservableProperty]
    private string _searchQuery = string.Empty;

    /// <summary>Optional fine-grained status filter applied on top of the sidebar view (TASK-134).</summary>
    [ObservableProperty]
    private DownloadStatusFilter _statusFilter = DownloadStatusFilter.Any;

    /// <summary>Optional "added since" date filter (TASK-134).</summary>
    [ObservableProperty]
    private DownloadDateFilter _dateFilter = DownloadDateFilter.AnyTime;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenewCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyLinkCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    private DownloadRowViewModel? _selectedDownload;

    public DownloadsListViewModel(
        IDownloadRepository repository,
        IDownloadManager manager,
        IDownloadActions actions,
        IClipboardService clipboard,
        IFileRevealer fileRevealer,
        IFileCategorizer categorizer,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(fileRevealer);
        ArgumentNullException.ThrowIfNull(categorizer);
        ArgumentNullException.ThrowIfNull(clock);
        _repository = repository;
        _manager = manager;
        _actions = actions;
        _clipboard = clipboard;
        _fileRevealer = fileRevealer;
        _categorizer = categorizer;
        _clock = clock;

        _manager.StatusChanged += OnStatusChanged;
        _manager.ProgressChanged += OnProgressChanged;
    }

    /// <summary>The rows shown in the list (the active filter applied), newest first.</summary>
    public ObservableCollection<DownloadRowViewModel> Downloads { get; } = new();

    /// <summary>Whether any downloads exist at all — drives the empty-state placeholder (filter-independent).</summary>
    public bool HasDownloads => _allRows.Count > 0;

    /// <summary>Show the spinner while the initial load runs.</summary>
    public bool ShowLoading => IsLoading;

    /// <summary>Show the error state when the load failed.</summary>
    public bool ShowError => !IsLoading && LoadError is not null;

    /// <summary>Show the first-run empty state (paste/drag hint) when there are no downloads at all.</summary>
    public bool ShowEmptyState => !IsLoading && LoadError is null && !HasDownloads;

    /// <summary>Show the "nothing in this view" state when a filter hides every download.</summary>
    public bool ShowFilteredEmpty => !IsLoading && LoadError is null && HasDownloads && Downloads.Count == 0;

    /// <summary>Show the grid only when there are rows to display.</summary>
    public bool ShowGrid => !IsLoading && LoadError is null && Downloads.Count > 0;

    private void RaiseListStateChanged()
    {
        OnPropertyChanged(nameof(HasDownloads));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowFilteredEmpty));
        OnPropertyChanged(nameof(ShowGrid));
    }

    partial void OnIsLoadingChanged(bool value) => RaiseListStateChanged();

    partial void OnLoadErrorChanged(string? value) => RaiseListStateChanged();

    /// <summary>The current live counts (total / per-category / status), for the sidebar badges (TASK-050).</summary>
    public DownloadCounts Counts { get; private set; } = DownloadCounts.Empty;

    /// <summary>Raised whenever <see cref="Counts"/> changes so the sidebar can refresh its badges.</summary>
    public event EventHandler? CountsChanged;

    /// <summary>Raised when the user asks to renew an expired download; the shell opens the renew dialog (TASK-053).</summary>
    public event EventHandler<DownloadRowViewModel>? RenewRequested;

    /// <summary>
    /// Raised (on the UI thread) once a freshly enqueued download's row exists (TASK-225). Rows are built
    /// asynchronously from the repository, so a consumer that reacts to the download *starting* can lose the
    /// race against its own row being created; this lets it wait for the row instead of polling.
    /// </summary>
    public event EventHandler<DownloadRowViewModel>? RowAdded;

    /// <summary>The live row for a download, or <see langword="null"/> if it isn't loaded (TASK-225).</summary>
    public DownloadRowViewModel? FindRow(long id) =>
        _byId.TryGetValue(id, out DownloadRowViewModel? row) ? row : null;

    /// <summary>Applies a sidebar filter, rebuilding the visible rows from the full set (TASK-050).</summary>
    public void ApplyFilter(DownloadFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _filter = filter;
        RebuildVisible();
    }

    /// <summary>The status options for the search bar's status dropdown (TASK-134).</summary>
    public IReadOnlyList<DownloadStatusFilter> StatusFilterOptions { get; } = Enum.GetValues<DownloadStatusFilter>();

    /// <summary>The date options for the search bar's date dropdown (TASK-134).</summary>
    public IReadOnlyList<DownloadDateFilter> DateFilterOptions { get; } = Enum.GetValues<DownloadDateFilter>();

    partial void OnSearchQueryChanged(string value) => RebuildVisible();

    partial void OnStatusFilterChanged(DownloadStatusFilter value) => RebuildVisible();

    partial void OnDateFilterChanged(DownloadDateFilter value) => RebuildVisible();

    /// <summary>
    /// Whether a row is shown: the sidebar view (TASK-050) plus the search bar's text, status, and date
    /// filters (TASK-134), all combined with AND.
    /// </summary>
    private bool IsVisible(DownloadRowViewModel row) =>
        _filter.Matches(row) && MatchesSearch(row) && MatchesStatus(row) && MatchesDate(row);

    private bool MatchesSearch(DownloadRowViewModel row)
    {
        string query = SearchQuery.Trim();
        if (query.Length == 0)
        {
            return true;
        }

        return row.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Url.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesStatus(DownloadRowViewModel row) => StatusFilter switch
    {
        DownloadStatusFilter.Any => true,
        DownloadStatusFilter.Queued => row.Status == DownloadStatus.Queued,
        DownloadStatusFilter.Active => row.Status == DownloadStatus.Active,
        DownloadStatusFilter.Paused => row.Status == DownloadStatus.Paused,
        DownloadStatusFilter.Completed => row.Status == DownloadStatus.Completed,
        DownloadStatusFilter.Failed => row.Status == DownloadStatus.Failed,
        DownloadStatusFilter.Expired => row.Status == DownloadStatus.Expired,
        _ => true,
    };

    private bool MatchesDate(DownloadRowViewModel row)
    {
        if (DateFilter == DownloadDateFilter.AnyTime)
        {
            return true;
        }

        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset cutoff = DateFilter switch
        {
            DownloadDateFilter.Today => new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero),
            DownloadDateFilter.Last7Days => now - TimeSpan.FromDays(7),
            DownloadDateFilter.Last30Days => now - TimeSpan.FromDays(30),
            _ => DateTimeOffset.MinValue,
        };

        return row.AddedSortKey >= cutoff;
    }

    /// <summary>Loads the persisted downloads into the list, applying any progress already seen this session.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            IReadOnlyList<Download> records = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(true);
            DateTimeOffset now = _clock.UtcNow;

            Downloads.Clear();
            _byId.Clear();
            _allRows.Clear();
            foreach (Download record in records)
            {
                DownloadRowViewModel row = CreateRow(record, now);
                if (_manager.GetProgress(record.Id) is { } progress)
                {
                    row.ApplyProgress(progress);
                }

                _allRows.Add(row);
                _byId[record.Id] = row;
            }

            RecomputeCounts();
            RebuildVisible();
            RaiseCounts();

            // Auto-select the most recent download so the detail pane has something to show on startup —
            // otherwise "Toggle details" looked broken (it only affects the pane once something is
            // selected, and a first-time user had nothing selected to notice the difference).
            SelectedDownload ??= Downloads.Count > 0 ? Downloads[0] : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Surface a deliberate error state with a retry rather than a blank list (UI bar §7). The message
            // is shown to the user (not swallowed); _ = ex keeps the analyzer happy without an unused warning.
            _ = ex;
            LoadError = "Couldn't load your downloads.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Retries the initial load after a failure (the error state's "Try again").</summary>
    [RelayCommand]
    private async Task RetryLoadAsync() => await LoadAsync().ConfigureAwait(true);

    private DownloadRowViewModel CreateRow(Download record, DateTimeOffset now)
    {
        FileCategory category = _categorizer.Categorize(record.Filename, contentType: null);
        return new DownloadRowViewModel(record, now, category);
    }

    private void OnStatusChanged(object? sender, DownloadStatusChangedEventArgs e)
    {
        // The initial enqueue (no previous status) needs the freshly-persisted record to build a row.
        if (e.Previous is null)
        {
            _ = AddNewlyEnqueuedAsync(e.DownloadId);
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_byId.TryGetValue(e.DownloadId, out DownloadRowViewModel? row))
            {
                row.ApplyStatus(e.Current);
                RefreshSelectionCommands(row);

                // Adjust only the completed/incomplete split (category and total are unchanged by a status
                // change) — +1/-1 from the previous→current status rather than a full rescan (TASK-108).
                bool wasCompleted = e.Previous == DownloadStatus.Completed;
                bool isCompleted = e.Current == DownloadStatus.Completed;
                if (wasCompleted != isCompleted)
                {
                    _completedCount += isCompleted ? 1 : -1;
                    _incompleteCount += isCompleted ? -1 : 1;
                }

                // A status change can move a row in/out of a status filter (O(1) check via _visibleIds).
                ReevaluateMembership(row);
                RaiseCounts();
            }
        });
    }

    private async Task AddNewlyEnqueuedAsync(long id)
    {
        Download? record;
        try
        {
            record = await _repository.GetAsync(id).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is a fire-and-forget continuation of a manager event; a failure here would otherwise be an
            // unobserved exception that silently drops the new row. Surface it via the list's error state
            // (mirrors LoadAsync) instead (no silent failures, §1). Marshal to the UI thread — the manager
            // raises StatusChanged off the UI thread.
            _ = ex;
            Dispatcher.UIThread.Post(() => LoadError = "Couldn't load a new download.");
            return;
        }

        if (record is null)
        {
            return;
        }

        DateTimeOffset now = _clock.UtcNow;
        Dispatcher.UIThread.Post(() =>
        {
            if (_byId.ContainsKey(id))
            {
                return;
            }

            DownloadRowViewModel row = CreateRow(record, now);
            _allRows.Insert(0, row); // newest first, matching the repository order
            _byId[id] = row;
            AddToCounts(row);
            RowAdded?.Invoke(this, row);
            if (IsVisible(row))
            {
                Downloads.Insert(0, row);
                _visibleIds.Add(id);

                // Auto-select every freshly enqueued download so the detail pane immediately shows its
                // progress — the point of a download manager is watching the thing you just started, not
                // hunting for it in the list (user-reported: a new download "only showed up in the list").
                SelectedDownload = row;
            }

            RaiseListStateChanged();
            RaiseCounts();
        });
    }

    private void OnProgressChanged(object? sender, DownloadProgressChangedEventArgs e)
    {
        DownloadProgress progress = e.Progress;
        Dispatcher.UIThread.Post(() =>
        {
            if (_byId.TryGetValue(e.DownloadId, out DownloadRowViewModel? row))
            {
                row.ApplyProgress(progress);
                RefreshSelectionCommands(row);
            }
        });
    }

    private void RefreshSelectionCommands(DownloadRowViewModel row)
    {
        // A status change on the selected row may flip which selection actions apply.
        if (ReferenceEquals(row, SelectedDownload))
        {
            ResumeCommand.NotifyCanExecuteChanged();
            PauseCommand.NotifyCanExecuteChanged();
            RenewCommand.NotifyCanExecuteChanged();
            OpenFileCommand.NotifyCanExecuteChanged();
        }

        // Any row's status change can flip whether "Stop all" has anything to stop.
        StopAllCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume() => _actions.Start(SelectedDownload!.Id);

    private bool CanResume() => SelectedDownload?.CanResume == true;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause() => _actions.Pause(SelectedDownload!.Id);

    private bool CanPause() => SelectedDownload?.CanPause == true;

    /// <summary>
    /// Stops every active download at once (the toolbar's "Stop" / Stop-all, TASK-052). Each active transfer
    /// is paused — cancelled with its checkpoint kept — so the set can be resumed later. Global by design:
    /// the per-download halt is <see cref="PauseCommand"/>, which the engine state machine makes equivalent
    /// (Active → Paused), so a distinct per-download "stop" would duplicate it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStopAll))]
    private void StopAll()
    {
        // Stop every active download, not just the ones visible under the current filter.
        foreach (DownloadRowViewModel row in _allRows)
        {
            if (row.IsDownloading)
            {
                _actions.Pause(row.Id);
            }
        }
    }

    private bool CanStopAll()
    {
        foreach (DownloadRowViewModel row in _allRows)
        {
            if (row.IsDownloading)
            {
                return true;
            }
        }

        return false;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveAsync()
    {
        DownloadRowViewModel row = SelectedDownload!;
        await _actions.RemoveAsync(row.Id).ConfigureAwait(true);
        Downloads.Remove(row);
        _visibleIds.Remove(row.Id);
        _allRows.Remove(row);
        _byId.Remove(row.Id);
        RemoveFromCounts(row);
        if (ReferenceEquals(SelectedDownload, row))
        {
            SelectedDownload = null;
        }

        RaiseListStateChanged();
        RaiseCounts();
    }

    [RelayCommand(CanExecute = nameof(CanRenew))]
    private void Renew() => RenewRequested?.Invoke(this, SelectedDownload!);

    private bool CanRenew() => SelectedDownload?.CanRenew == true;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopyLinkAsync() => await _clipboard.CopyAsync(SelectedDownload!.Url).ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanOpenFile))]
    private void OpenFile() => _fileRevealer.OpenFile(SelectedDownload!.FilePath);

    private bool CanOpenFile() => SelectedDownload?.CanOpenFile == true;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenFolder() => _fileRevealer.RevealInFolder(SelectedDownload!.FilePath);

    private bool HasSelection() => SelectedDownload is not null;

    /// <summary>Rebuilds the visible collection from the full set under the current filter.</summary>
    private void RebuildVisible()
    {
        Downloads.Clear();
        _visibleIds.Clear();
        foreach (DownloadRowViewModel row in _allRows)
        {
            if (IsVisible(row))
            {
                Downloads.Add(row);
                _visibleIds.Add(row.Id);
            }
        }

        RaiseListStateChanged();
    }

    /// <summary>
    /// After a row's status changes, adds or removes it from the visible collection to match the active
    /// filter — preserving order and selection rather than rebuilding the whole list on every event.
    /// </summary>
    private void ReevaluateMembership(DownloadRowViewModel row)
    {
        bool shouldShow = IsVisible(row);
        bool wasVisible = _visibleIds.Contains(row.Id);
        if (shouldShow == wasVisible)
        {
            return; // common case: visibility unchanged — no list scan or mutation (TASK-108)
        }

        if (shouldShow)
        {
            Downloads.Insert(VisibleInsertIndex(row), row);
            _visibleIds.Add(row.Id);
        }
        else
        {
            Downloads.Remove(row);
            _visibleIds.Remove(row.Id);
        }

        RaiseListStateChanged();
    }

    /// <summary>The index in <see cref="Downloads"/> that keeps it in the same (newest-first) order as the full set.</summary>
    private int VisibleInsertIndex(DownloadRowViewModel row)
    {
        int masterIndex = _allRows.IndexOf(row);
        int insertAt = 0;
        for (int i = 0; i < masterIndex; i++)
        {
            if (IsVisible(_allRows[i]))
            {
                insertAt++;
            }
        }

        return insertAt;
    }

    private void RaiseCounts()
    {
        Counts = ComputeCounts();
        OnPropertyChanged(nameof(Counts));
        CountsChanged?.Invoke(this, EventArgs.Empty);
    }

    // Snapshot the running counts (a fresh dictionary so a later mutation can't corrupt an emitted snapshot).
    private DownloadCounts ComputeCounts() =>
        new(_allRows.Count, new Dictionary<FileCategory, int>(_categoryCounts), _incompleteCount, _completedCount);

    /// <summary>Rebuilds the running counts from the full set — one O(n) pass, used only on load.</summary>
    private void RecomputeCounts()
    {
        _categoryCounts.Clear();
        _completedCount = 0;
        _incompleteCount = 0;
        foreach (DownloadRowViewModel row in _allRows)
        {
            AddToCounts(row);
        }
    }

    private void AddToCounts(DownloadRowViewModel row)
    {
        _categoryCounts[row.Category] = _categoryCounts.GetValueOrDefault(row.Category) + 1;
        if (row.IsCompleted)
        {
            _completedCount++;
        }
        else
        {
            _incompleteCount++;
        }
    }

    private void RemoveFromCounts(DownloadRowViewModel row)
    {
        if (_categoryCounts.TryGetValue(row.Category, out int n))
        {
            if (n <= 1)
            {
                _categoryCounts.Remove(row.Category);
            }
            else
            {
                _categoryCounts[row.Category] = n - 1;
            }
        }

        if (row.IsCompleted)
        {
            _completedCount--;
        }
        else
        {
            _incompleteCount--;
        }
    }

    public void Dispose()
    {
        _manager.StatusChanged -= OnStatusChanged;
        _manager.ProgressChanged -= OnProgressChanged;
    }
}
