using System.Net.Http;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustDownload.App.Services;
using JustDownload.App.Services.YouTube;
using JustDownload.Core.Media;
using JustDownload.Core.Media.YtDlp;
using JustDownload.Core.Settings;
using Microsoft.Extensions.Logging;

namespace JustDownload.App.ViewModels.Settings;

/// <summary>
/// Video settings (TASK-162, locked decision D3): the master "enable video capture/detection" toggle —
/// off by default, gating whether the optional yt-dlp fallback is available at all — and, once it's on, a
/// "Download yt-dlp" button that downloads the pinned release, verifies its checksum, and self-validates by
/// running <c>yt-dlp --version</c> through <see cref="IYtDlpProvisioner"/>. The same click also provisions
/// Deno (<see cref="IDenoProvisioner"/>) — the JS runtime yt-dlp needs to solve YouTube's signature/JS
/// challenges — since it's a dependency of yt-dlp actually working well, not a separate feature; no second
/// button. A Deno failure never fails the yt-dlp result: it degrades gracefully into <see cref="DenoWarning"/>.
/// </summary>
public sealed partial class VideoSettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IYtDlpLocator _locator;
    private readonly IYtDlpProvisioner _provisioner;
    private readonly IDenoProvisioner _denoProvisioner;
    private readonly ILogger<VideoSettingsViewModel> _logger;
    private readonly IYouTubeSignInService _signIn;
    private readonly IYouTubeSessionStore _sessionStore;
    private readonly IYouTubeSignInConsentGate _consentGate;
    private bool _suppress;

    [ObservableProperty]
    private bool _videoCaptureEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private YtDlpStatus _status = YtDlpStatus.Checking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _version;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Non-fatal Deno provisioning outcome shown alongside the yt-dlp status; null when Deno is
    /// ready or was never attempted. yt-dlp itself still works without Deno, just with weaker YouTube
    /// coverage — see the class summary.</summary>
    [ObservableProperty]
    private string? _denoWarning;

    /// <summary>Whether an app-exclusive "Sign in to YouTube" session (TASK-...) is currently stored.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignOutOfYouTubeCommand))]
    private bool _hasYouTubeSession;

    /// <summary>Result of the most recent sign-in/sign-out action, shown beside the buttons.</summary>
    [ObservableProperty]
    private string? _youTubeSignInStatusMessage;

    public VideoSettingsViewModel(
        ISettingsService settings,
        IYtDlpLocator locator,
        IYtDlpProvisioner provisioner,
        IDenoProvisioner denoProvisioner,
        ILogger<VideoSettingsViewModel> logger,
        IYouTubeSignInService signIn,
        IYouTubeSessionStore sessionStore,
        IYouTubeSignInConsentGate consentGate)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(denoProvisioner);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(signIn);
        ArgumentNullException.ThrowIfNull(sessionStore);
        ArgumentNullException.ThrowIfNull(consentGate);
        _settings = settings;
        _locator = locator;
        _provisioner = provisioner;
        _denoProvisioner = denoProvisioner;
        _logger = logger;
        _signIn = signIn;
        _sessionStore = sessionStore;
        _consentGate = consentGate;

        _suppress = true;
        _videoCaptureEnabled = settings.Current.VideoCaptureEnabled;
        _suppress = false;

        _hasYouTubeSession = sessionStore.HasSession;

        _ = RefreshStatusAsync();
    }

    /// <summary>Whether this OS can host the "Sign in to YouTube" modal at all (Windows only, v1).</summary>
    public bool IsYouTubeSignInSupported => _signIn.IsSupported;

    /// <summary>Human-readable yt-dlp status shown beside the download button.</summary>
    public string StatusText => Status switch
    {
        YtDlpStatus.Checking => "Checking…",
        YtDlpStatus.NotInstalled => "Not installed",
        YtDlpStatus.Downloading => "Downloading…",
        YtDlpStatus.Ready => Version is null ? "Ready" : $"Ready (yt-dlp {Version})",
        YtDlpStatus.Error => "Error",
        _ => string.Empty,
    };

    partial void OnVideoCaptureEnabledChanged(bool value)
    {
        if (!_suppress)
        {
            _ = _settings.UpdateAsync(s => s with { VideoCaptureEnabled = value });
        }
    }

    /// <summary>Checks whether yt-dlp is already located, without downloading anything.</summary>
    private async Task RefreshStatusAsync()
    {
        YtDlpInfo? info = await _locator.LocateAsync().ConfigureAwait(true);
        Status = info is null ? YtDlpStatus.NotInstalled : YtDlpStatus.Ready;
        Version = info?.Version;
    }

    private bool CanDownload => Status != YtDlpStatus.Downloading;

    /// <summary>Downloads (or confirms) yt-dlp and self-validates it, reporting Ready/Error (AC1). Also
    /// provisions Deno afterwards — failures there are reported via <see cref="DenoWarning"/> without
    /// affecting the yt-dlp result (AC: Deno is a best-effort dependency, never a hard requirement).</summary>
    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync(CancellationToken cancellationToken)
    {
        Status = YtDlpStatus.Downloading;
        ErrorMessage = null;
        DenoWarning = null;
        try
        {
            YtDlpInfo? info = await _provisioner.EnsureAsync(cancellationToken).ConfigureAwait(true);
            if (info is null)
            {
                Status = YtDlpStatus.Error;
                ErrorMessage = "No yt-dlp build is available for this platform.";
                return;
            }

            Status = YtDlpStatus.Ready;
            Version = info.Version;
        }
        catch (Exception ex) when (ex is YtDlpException or IOException or HttpRequestException)
        {
            Status = YtDlpStatus.Error;
            ErrorMessage = ex.Message;
            return;
        }

        await ProvisionDenoAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task ProvisionDenoAsync(CancellationToken cancellationToken)
    {
        try
        {
            DenoInfo? deno = await _denoProvisioner.EnsureAsync(cancellationToken).ConfigureAwait(true);
            if (deno is null)
            {
                LogNoDenoSource(_logger);
            }
        }
        catch (Exception ex) when (ex is DenoException or IOException or HttpRequestException)
        {
            // Never fail the yt-dlp result over this: yt-dlp still works, just without a JS runtime for
            // sites that need one (CLAUDE.md §5: no silent failures — surfaced via DenoWarning instead).
            LogDenoProvisioningFailed(_logger, ex);
            DenoWarning = $"yt-dlp is ready, but Deno (needed for some YouTube formats) failed to download: {ex.Message}";
        }
    }

    /// <summary>
    /// Runs the one-time consent notice (if not already acknowledged) then the "Sign in to YouTube" modal.
    /// Called from <c>SettingsWindow</c>'s code-behind (like the folder-picker/import-export actions
    /// beside it) because it needs a real <see cref="Window"/> to own the modal — a top-level concern that
    /// cannot live in this view-model. A no-op if <see cref="IsYouTubeSignInSupported"/> is false.
    /// </summary>
    public async Task SignInToYouTubeAsync(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!IsYouTubeSignInSupported)
        {
            return;
        }

        YouTubeSignInStatusMessage = null;
        if (!await _consentGate.ConfirmAsync().ConfigureAwait(true))
        {
            return;
        }

        try
        {
            YouTubeSignInResult result = await _signIn.SignInAsync(owner).ConfigureAwait(true);
            HasYouTubeSession = _sessionStore.HasSession;
            YouTubeSignInStatusMessage = result.Outcome switch
            {
                YouTubeSignInOutcome.Succeeded => "Signed in — an app-exclusive session is stored.",
                YouTubeSignInOutcome.Failed => result.ErrorMessage,
                _ => YouTubeSignInStatusMessage,
            };
        }
        catch (Exception ex)
        {
            // Last-resort catch — should never reach here given the layers above, but prevents a crash.
            LogYouTubeSignInFailed(_logger, ex);
            YouTubeSignInStatusMessage = $"Sign-in failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasYouTubeSession))]
    private async Task SignOutOfYouTubeAsync()
    {
        await _sessionStore.ClearAsync().ConfigureAwait(true);
        await _signIn.SignOutAsync().ConfigureAwait(true);
        HasYouTubeSession = false;
        YouTubeSignInStatusMessage = "Signed out — the app-exclusive session was removed.";
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "No Deno build available for this platform; yt-dlp will run without a JS runtime.")]
    private static partial void LogNoDenoSource(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "YouTube sign-in failed unexpectedly.")]
    private static partial void LogYouTubeSignInFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Deno provisioning failed after a successful yt-dlp download.")]
    private static partial void LogDenoProvisioningFailed(ILogger logger, Exception exception);
}

/// <summary>yt-dlp availability shown in the Video settings section (TASK-162, AC1).</summary>
public enum YtDlpStatus
{
    /// <summary>Locating any existing install, on view-model construction.</summary>
    Checking,

    /// <summary>No working yt-dlp was found; the user can click "Download yt-dlp".</summary>
    NotInstalled,

    /// <summary>A download + verify + self-validate is in progress.</summary>
    Downloading,

    /// <summary>yt-dlp is present and self-validated (ran <c>--version</c> successfully).</summary>
    Ready,

    /// <summary>The download or integrity check failed; see <see cref="VideoSettingsViewModel.ErrorMessage"/>.</summary>
    Error,
}
