using Avalonia.Threading;
using JustDownload.App.ViewModels;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Settings;

namespace JustDownload.App.Services;

/// <summary>
/// Opens and tracks the standalone per-download progress windows (TASK-225). A window appears whenever a
/// download starts or is resumed and follows it to completion, so progress is visible without the main window
/// — the behaviour users expect from a download manager.
/// <para>
/// Deliberately headless: it never touches <c>Window</c> itself but drives a presenter callback, so the
/// open/focus/close policy here is unit-testable without a UI (§3). <see cref="App"/> supplies the callback
/// that actually shows a <c>DownloadProgressWindow</c>.
/// </para>
/// </summary>
public sealed class DownloadProgressWindowService : IDisposable
{
    /// <summary>
    /// Upper bound on windows open at once. "Resume all" over a large queue would otherwise carpet the desktop
    /// with windows the user never asked for individually; past this point the main window remains the place
    /// to watch the rest. Chosen to comfortably exceed the default <c>MaxConcurrentDownloads</c> (4).
    /// </summary>
    public const int MaxOpenWindows = 8;

    private readonly IDownloadManager _manager;
    private readonly ISettingsService _settings;
    private readonly DownloadsListViewModel _list;
    private readonly Func<DownloadRowViewModel, DownloadProgressViewModel> _createViewModel;
    private readonly Action<DownloadProgressViewModel> _present;
    private readonly Dictionary<long, DownloadProgressViewModel> _open = [];
    private readonly HashSet<long> _awaitingRow = [];
    private bool _disposed;

    public DownloadProgressWindowService(
        IDownloadManager manager,
        ISettingsService settings,
        DownloadsListViewModel list,
        Func<DownloadRowViewModel, DownloadProgressViewModel> createViewModel,
        Action<DownloadProgressViewModel> present)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(createViewModel);
        ArgumentNullException.ThrowIfNull(present);
        _manager = manager;
        _settings = settings;
        _list = list;
        _createViewModel = createViewModel;
        _present = present;
    }

    /// <summary>The downloads that currently have a progress window open.</summary>
    public IReadOnlyCollection<long> OpenDownloadIds => _open.Keys;

    /// <summary>The open window for a download, or <see langword="null"/>. Lets the taskbar flash (TASK-226)
    /// target the window the user is actually watching rather than the main window.</summary>
    public DownloadProgressViewModel? Find(long downloadId) =>
        _open.TryGetValue(downloadId, out DownloadProgressViewModel? vm) ? vm : null;

    /// <summary>Begins opening progress windows as downloads start.</summary>
    public void Start()
    {
        _manager.StatusChanged += OnStatusChanged;
        _list.RowAdded += OnRowAdded;
    }

    private void OnStatusChanged(object? sender, DownloadStatusChangedEventArgs e)
    {
        // Only the transition *into* Active is an intent to watch: a start or a resume. The enqueue itself
        // (Previous is null) is not — "Add paused" must not pop a window for a download that isn't running.
        if (e.Current != DownloadStatus.Active || e.Previous == DownloadStatus.Active)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => EnsureWindow(e.DownloadId));
    }

    /// <summary>
    /// Opens (or re-focuses) the progress window for a download. A brand-new download's row is created
    /// asynchronously and can still be missing here, so we note the id and let <see cref="OnRowAdded"/>
    /// finish the job once the row exists.
    /// </summary>
    public void EnsureWindow(long downloadId)
    {
        if (_disposed || !_settings.Current.ShowDownloadProgressWindow)
        {
            return;
        }

        if (_open.TryGetValue(downloadId, out DownloadProgressViewModel? existing))
        {
            _present(existing); // already open — bring it forward rather than stacking a duplicate
            return;
        }

        if (_list.FindRow(downloadId) is not { } row)
        {
            _awaitingRow.Add(downloadId);
            return;
        }

        Open(row);
    }

    private void OnRowAdded(object? sender, DownloadRowViewModel row)
    {
        if (_awaitingRow.Remove(row.Id) && !_disposed && _settings.Current.ShowDownloadProgressWindow)
        {
            Open(row);
        }
    }

    private void Open(DownloadRowViewModel row)
    {
        if (_open.Count >= MaxOpenWindows)
        {
            return;
        }

        DownloadProgressViewModel viewModel = _createViewModel(row);
        _open[row.Id] = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
        _present(viewModel);
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        if (sender is DownloadProgressViewModel viewModel)
        {
            Forget(viewModel);
        }
    }

    /// <summary>
    /// Drops a window from the tracked set — called both when the view-model asks to close and when the user
    /// closes the window directly, so a later restart of that download opens a fresh window instead of
    /// silently doing nothing.
    /// </summary>
    public void Forget(DownloadProgressViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        viewModel.CloseRequested -= OnCloseRequested;
        _open.Remove(viewModel.Row.Id);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _manager.StatusChanged -= OnStatusChanged;
        _list.RowAdded -= OnRowAdded;
        foreach (DownloadProgressViewModel viewModel in _open.Values)
        {
            viewModel.CloseRequested -= OnCloseRequested;
            viewModel.Dispose();
        }

        _open.Clear();
    }
}
