using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustDownload.Core.Media;
using JustDownload.Core.Settings;

namespace JustDownload.App.ViewModels.Settings;

/// <summary>
/// Video settings (TASK-162, locked decision D3): the master "enable video capture/detection" toggle —
/// off by default, gating whether the optional yt-dlp fallback is available at all — and, once it's on, a
/// "Download yt-dlp" button that downloads the pinned release, verifies its checksum, and self-validates by
/// running <c>yt-dlp --version</c> through <see cref="IYtDlpProvisioner"/>.
/// </summary>
public sealed partial class VideoSettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IYtDlpLocator _locator;
    private readonly IYtDlpProvisioner _provisioner;
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

    public VideoSettingsViewModel(ISettingsService settings, IYtDlpLocator locator, IYtDlpProvisioner provisioner)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(provisioner);
        _settings = settings;
        _locator = locator;
        _provisioner = provisioner;

        _suppress = true;
        _videoCaptureEnabled = settings.Current.VideoCaptureEnabled;
        _suppress = false;

        _ = RefreshStatusAsync();
    }

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

    /// <summary>Downloads (or confirms) yt-dlp and self-validates it, reporting Ready/Error (AC1).</summary>
    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync(CancellationToken cancellationToken)
    {
        Status = YtDlpStatus.Downloading;
        ErrorMessage = null;
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
        }
    }
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
