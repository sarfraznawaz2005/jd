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

    /// <summary>The rows shown in the list, newest first (the repository returns them in that order).</summary>
    public ObservableCollection<DownloadRowViewModel> Downloads { get; } = new();

    /// <summary>Whether any downloads exist — drives the empty-state placeholder.</summary>
    public bool HasDownloads => Downloads.Count > 0;

    /// <summary>Raised when the user asks to renew an expired download; the shell opens the renew dialog (TASK-053).</summary>
    public event EventHandler<DownloadRowViewModel>? RenewRequested;

    /// <summary>Loads the persisted downloads into the list, applying any progress already seen this session.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Download> records = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(true);
        DateTimeOffset now = _clock.UtcNow;

        Downloads.Clear();
        _byId.Clear();
        foreach (Download record in records)
        {
            DownloadRowViewModel row = CreateRow(record, now);
            if (_manager.GetProgress(record.Id) is { } progress)
            {
                row.ApplyProgress(progress);
            }

            Downloads.Add(row);
            _byId[record.Id] = row;
        }

        OnPropertyChanged(nameof(HasDownloads));
    }

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
            }
        });
    }

    private async Task AddNewlyEnqueuedAsync(long id)
    {
        Download? record = await _repository.GetAsync(id).ConfigureAwait(true);
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
            Downloads.Insert(0, row); // newest first, matching the repository order
            _byId[id] = row;
            OnPropertyChanged(nameof(HasDownloads));
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
        foreach (DownloadRowViewModel row in Downloads)
        {
            if (row.IsDownloading)
            {
                _actions.Pause(row.Id);
            }
        }
    }

    private bool CanStopAll()
    {
        foreach (DownloadRowViewModel row in Downloads)
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
        _byId.Remove(row.Id);
        if (ReferenceEquals(SelectedDownload, row))
        {
            SelectedDownload = null;
        }

        OnPropertyChanged(nameof(HasDownloads));
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

    public void Dispose()
    {
        _manager.StatusChanged -= OnStatusChanged;
        _manager.ProgressChanged -= OnProgressChanged;
    }
}
