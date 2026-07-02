using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Settings;
using JustDownload.Core.Updates;

namespace JustDownload.App.ViewModels.Settings;

/// <summary>
/// Updates settings (TASK-080, PRD 6.3): the opt-in "check for updates" toggle — off by default (AC0) —
/// and, once it's on, a manual "Check for Updates" button that runs <see cref="IUpdateChecker"/> and
/// reports the outcome. Mirrors <see cref="VideoSettingsViewModel"/>'s toggle + button + status UX for
/// consistency. Because <see cref="IUpdateChecker"/> itself refuses to make a network call while the toggle
/// is off (AC2), the button is only shown once the toggle is on — defense in depth, not the only guard.
/// </summary>
public sealed partial class UpdateSettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IUpdateChecker _checker;
    private bool _suppress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    private bool _autoUpdateEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    private bool _isChecking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private UpdateCheckStatus? _lastStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _latestVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _errorMessage;

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
    }

    /// <summary>The running app's version, shown next to the toggle.</summary>
    public string CurrentVersion { get; }

    /// <summary>Human-readable status shown beside the check button.</summary>
    public string StatusText => (IsChecking, LastStatus) switch
    {
        (true, _) => "Checking…",
        (false, null) => string.Empty,
        (false, UpdateCheckStatus.UpToDate) => "You're up to date.",
        (false, UpdateCheckStatus.AvailableForManualDownload) =>
            $"Update {LatestVersion} is available — download it from GitHub.",
        (false, UpdateCheckStatus.Applied) => $"Update {LatestVersion} downloaded — installer launched.",
        (false, UpdateCheckStatus.RejectedUnsigned) => "Update rejected: the release isn't signed.",
        (false, UpdateCheckStatus.RejectedInvalidSignature) => "Update rejected: signature verification failed.",
        (false, UpdateCheckStatus.RejectedAssetHashMismatch) =>
            "Update rejected: the download didn't match its signed checksum.",
        (false, UpdateCheckStatus.NotConfigured) => "Update checking isn't configured yet.",
        (false, UpdateCheckStatus.Disabled) => string.Empty,
        (false, UpdateCheckStatus.Error) => $"Update check failed: {ErrorMessage}",
        _ => string.Empty,
    };

    partial void OnAutoUpdateEnabledChanged(bool value)
    {
        if (!_suppress)
        {
            _ = _settings.UpdateAsync(s => s with { AutoUpdateEnabled = value });
        }
    }

    private bool CanCheckForUpdates => AutoUpdateEnabled && !IsChecking;

    /// <summary>Runs one check and reports the outcome (AC1).</summary>
    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        IsChecking = true;
        try
        {
            UpdateCheckResult result = await _checker.CheckAsync(cancellationToken).ConfigureAwait(true);
            LastStatus = result.Status;
            LatestVersion = result.LatestVersion;
            ErrorMessage = result.ErrorMessage;
        }
        finally
        {
            IsChecking = false;
        }
    }
}
