using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustDownload.App.Services;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Settings;

namespace JustDownload.App.ViewModels;

/// <summary>
/// One standalone download-progress window (TASK-225, US-15c): the same Download/Options/Connections content
/// the docked detail pane shows — hence the shared <see cref="DownloadDetailViewModel"/> — wrapped in the
/// window-level behaviour that pane has no need for. It follows a single download from start to finish and
/// then switches to a terminal state offering Open file / Open folder, so a user who has the app in the
/// background still sees the download through (the classic download-manager progress dialog).
/// <para>
/// The view-model owns its <see cref="Detail"/> instance and disposes it with the window, so each open window
/// samples and repaints only its own download.
/// </para>
/// </summary>
public sealed partial class DownloadProgressViewModel : ViewModelBase, IDisposable
{
    private readonly IFileRevealer _fileRevealer;
    private readonly ISettingsService _settings;
    private bool _disposed;

    /// <summary>
    /// Whether the window closes itself the moment the download finishes. Bound to the window's checkbox and
    /// persisted (TASK-225), so the choice survives restarts.
    /// </summary>
    [ObservableProperty]
    private bool _closeWhenDone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    [NotifyPropertyChangedFor(nameof(IsComplete))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(OutcomeLabel))]
    [NotifyCanExecuteChangedFor(nameof(OpenFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    private DownloadStatus _status;

    /// <summary>
    /// Whether this window shows the collapsed, tabs-free status bar instead of the full
    /// Download/Options/Connections view. Deliberately per-window and session-only — not persisted — so each
    /// download the user is watching can be sized independently, and a fresh window always opens in the full
    /// view. The window's code-behind reacts to this to snap its height to fit each mode.
    /// </summary>
    [ObservableProperty]
    private bool _isCompact;

    public DownloadProgressViewModel(
        DownloadRowViewModel row,
        DownloadDetailViewModel detail,
        IFileRevealer fileRevealer,
        ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(fileRevealer);
        ArgumentNullException.ThrowIfNull(settings);
        Row = row;
        Detail = detail;
        _fileRevealer = fileRevealer;
        _settings = settings;

        _closeWhenDone = settings.Current.CloseProgressWindowWhenDone;
        _status = row.Status;

        Detail.Select(row);
        Detail.Start();
        Row.PropertyChanged += OnRowPropertyChanged;
    }

    /// <summary>The download this window follows.</summary>
    public DownloadRowViewModel Row { get; }

    /// <summary>The Download/Options/Connections content, shared with the docked detail pane's view.</summary>
    public DownloadDetailViewModel Detail { get; }

    /// <summary>Raised when the window should close itself (the Close button, or an auto-close on completion).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The window title — the file being downloaded.</summary>
    public string Title => Row.FileName;

    /// <summary>Whether the download reached a terminal state, so the footer shows the outcome actions.</summary>
    public bool IsFinished => Status is DownloadStatus.Completed or DownloadStatus.Failed;

    public bool IsComplete => Status == DownloadStatus.Completed;

    public bool IsFailed => Status == DownloadStatus.Failed;

    /// <summary>The terminal-state banner text; empty while the download is still in flight.</summary>
    public string OutcomeLabel => Status switch
    {
        DownloadStatus.Completed => "Download complete",
        DownloadStatus.Failed => "Download failed",
        _ => string.Empty,
    };

    /// <summary>Re-reads the live status from the row. Called on row changes; public so tests can drive it.</summary>
    public void Refresh()
    {
        Status = Row.Status;
        if (Status == DownloadStatus.Completed && CloseWhenDone)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadRowViewModel.Status))
        {
            Refresh();
        }
    }

    partial void OnCloseWhenDoneChanged(bool value) =>
        _ = _settings.UpdateAsync(s => s with { CloseProgressWindowWhenDone = value });

    [RelayCommand(CanExecute = nameof(CanOpenFile))]
    private void OpenFile() => _fileRevealer.OpenFile(Row.FilePath);

    private bool CanOpenFile() => IsComplete && Row.FilePath is { Length: > 0 };

    [RelayCommand(CanExecute = nameof(CanOpenFile))]
    private void OpenFolder() => _fileRevealer.RevealInFolder(Row.FilePath);

    /// <summary>
    /// Closes the window. Deliberately does not touch the transfer — a user dismissing the progress window is
    /// hiding the view, not cancelling the download (Pause/Cancel in the tab footer do that).
    /// </summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Flips between the full and collapsed views.</summary>
    [RelayCommand]
    private void ToggleCompact() => IsCompact = !IsCompact;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Row.PropertyChanged -= OnRowPropertyChanged;
        Detail.Dispose();
    }
}
