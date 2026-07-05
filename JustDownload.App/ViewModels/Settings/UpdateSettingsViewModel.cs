using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Settings;
using JustDownload.Core.Updates;

namespace JustDownload.App.ViewModels.Settings;

/// <summary>
/// Updates settings (TASK-080/TASK-223, PRD 6.3): the opt-in "check for updates" toggle — off by default
/// (AC0) — and, once it's on, "Check for Updates" (detect-only) followed by an explicit "Update Now"
/// confirmation before anything is downloaded, with a progress bar and a Cancel option while it runs.
/// Mirrors <see cref="VideoSettingsViewModel"/>'s toggle + button + status UX for consistency. Because
/// <see cref="IUpdateChecker"/> itself refuses to make a network call while the toggle is off (AC2), the
/// buttons are only shown once the toggle is on — defense in depth, not the only guard.
/// </summary>
public sealed partial class UpdateSettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IUpdateChecker _checker;
    private bool _suppress;
    private UpdateCheckResult? _pendingResult;
    private CancellationTokenSource? _downloadCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    private bool _autoUpdateEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateNowCommand))]
    private bool _isChecking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelDownloadCommand))]
    private bool _isDownloading;

    /// <summary>0-100 download completion, bound to the Updates section's progress bar (TASK-223).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private double _downloadProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsStatusError))]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable))]
    [NotifyCanExecuteChangedFor(nameof(UpdateNowCommand))]
    private UpdateCheckStatus? _lastStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _latestVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _errorMessage;

    /// <summary>Raised once the installer has launched and the brief confirmation message has been shown
    /// (TASK-223) — bubbled up through <see cref="SettingsViewModel"/> so the app can quit.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>Whether <see cref="StatusText"/> currently describes a failure (network error or a
    /// security rejection) rather than a neutral/success outcome — drives the red banner treatment in
    /// the view instead of the quiet dim-text default.</summary>
    public bool IsStatusError => LastStatus is UpdateCheckStatus.Error or UpdateCheckStatus.RejectedUnsigned
        or UpdateCheckStatus.RejectedInvalidSignature or UpdateCheckStatus.RejectedAssetHashMismatch;

    /// <summary>Whether a verified update is ready to download — shows the "Update Now" button (TASK-223).</summary>
    public bool IsUpdateAvailable => LastStatus == UpdateCheckStatus.Available;

    public UpdateSettingsViewModel(ISettingsService settings, IUpdateChecker checker, IAppVersionProvider versionProvider)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(checker);
        ArgumentNullException.ThrowIfNull(versionProvider);
        _settings = settings;
        _checker = checker;
        CurrentVersion = versionProvider.CurrentVersion;

        _suppress = true;
        _autoUpdateEnabled = settings.Current.AutoUpdateEnabled;
        _suppress = false;

        // Pick up a result the one-shot startup check already found (TASK-223), so opening Settings shows
        // it immediately instead of requiring another manual "Check for Updates" click.
        if (checker.LastResult is { } lastResult)
        {
            ApplyResult(lastResult);
        }
    }

    /// <summary>The running app's version, shown next to the toggle.</summary>
    public string CurrentVersion { get; }

    /// <summary>Human-readable status shown beside the check/update buttons.</summary>
    public string StatusText => (IsDownloading, IsChecking, LastStatus) switch
    {
        (true, _, _) => $"Downloading update… {DownloadProgress:F0}%",
        (_, true, _) => "Checking…",
        (_, _, null) => string.Empty,
        (_, _, UpdateCheckStatus.UpToDate) => "You're up to date.",
        (_, _, UpdateCheckStatus.Available) => $"Update {LatestVersion} is available.",
        (_, _, UpdateCheckStatus.AvailableForManualDownload) =>
            $"Update {LatestVersion} is available — download it from GitHub.",
        (_, _, UpdateCheckStatus.Applied) => "Installer launched — closing JustDownload…",
        (_, _, UpdateCheckStatus.RejectedUnsigned) => "Update rejected: the release isn't signed.",
        (_, _, UpdateCheckStatus.RejectedInvalidSignature) => "Update rejected: signature verification failed.",
        (_, _, UpdateCheckStatus.RejectedAssetHashMismatch) =>
            "Update rejected: the download didn't match its signed checksum.",
        (_, _, UpdateCheckStatus.NotConfigured) => "Update checking isn't configured yet.",
        (_, _, UpdateCheckStatus.Disabled) => string.Empty,
        (_, _, UpdateCheckStatus.Error) => $"Update check failed: {ErrorMessage}",
        _ => string.Empty,
    };

    partial void OnAutoUpdateEnabledChanged(bool value)
    {
        if (!_suppress)
        {
            _ = _settings.UpdateAsync(s => s with { AutoUpdateEnabled = value });
        }
    }

    private bool CanCheckForUpdates => AutoUpdateEnabled && !IsChecking && !IsDownloading;

    /// <summary>Runs one detect-only check and reports the outcome (AC1) — never downloads or launches anything.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        IsChecking = true;
        try
        {
            UpdateCheckResult result = await _checker.CheckAsync(cancellationToken).ConfigureAwait(true);
            ApplyResult(result);
        }
        finally
        {
            IsChecking = false;
        }
    }

    private bool CanUpdateNow => IsUpdateAvailable && !IsDownloading && !IsChecking;

    /// <summary>Downloads the verified installer (with progress) and launches it, only once the user
    /// explicitly confirms (TASK-223) — then quits the app after a brief confirmation message.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateNow))]
    private async Task UpdateNowAsync()
    {
        if (_pendingResult is not { } pendingResult)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _downloadCts = cts;
        IsDownloading = true;
        DownloadProgress = 0;
        var progress = new Progress<double>(fraction => DownloadProgress = Math.Clamp(fraction * 100, 0, 100));
        try
        {
            UpdateCheckResult result = await _checker
                .DownloadAndApplyAsync(pendingResult, progress, cts.Token).ConfigureAwait(true);
            ApplyResult(result);

            if (result.Status == UpdateCheckStatus.Applied)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1500), CancellationToken.None).ConfigureAwait(true);
                QuitRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // User-cancelled: leave LastStatus/_pendingResult as they were (still Available) so
            // "Update Now" reappears rather than looking like a failure.
        }
        finally
        {
            if (ReferenceEquals(_downloadCts, cts))
            {
                IsDownloading = false;
                _downloadCts = null;
            }

            cts.Dispose();
        }
    }

    private bool CanCancelDownload => IsDownloading;

    /// <summary>Cancels an in-progress download — instant, matching the project's pause/cancel standard.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelDownload))]
    private void CancelDownload() => _downloadCts?.Cancel();

    private void ApplyResult(UpdateCheckResult result)
    {
        LastStatus = result.Status;
        LatestVersion = result.LatestVersion;
        ErrorMessage = result.ErrorMessage;
        _pendingResult = result.Status == UpdateCheckStatus.Available ? result : null;
    }
}
