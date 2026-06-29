using CommunityToolkit.Mvvm.ComponentModel;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.Core.Settings;

namespace JustDownload.App.ViewModels.Settings;

/// <summary>
/// General settings (TASK-057): theme, UI density, and the default media quality/container. Theme switches
/// live through the <see cref="IThemeService"/>; every value persists through the <see cref="ISettingsService"/>
/// so it survives a restart. A suppress flag stops the initial hydration from writing back.
/// </summary>
public sealed partial class GeneralSettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private bool _suppress;

    [ObservableProperty]
    private ThemeMode _selectedTheme;

    [ObservableProperty]
    private UiDensity _density;

    [ObservableProperty]
    private VideoQuality _defaultVideoQuality;

    [ObservableProperty]
    private MediaContainer _defaultContainer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefaultDownloadFolderError))]
    [NotifyPropertyChangedFor(nameof(DefaultDownloadFolderHint))]
    private string _defaultDownloadFolder;

    [ObservableProperty]
    private bool _startMinimizedToTray;

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private bool _monitorClipboard;

    public GeneralSettingsViewModel(ISettingsService settings, IThemeService theme)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(theme);
        _settings = settings;
        _theme = theme;

        _suppress = true;
        AppSettings current = settings.Current;
        _selectedTheme = theme.Mode;
        _density = current.Density;
        _defaultVideoQuality = current.DefaultVideoQuality;
        _defaultContainer = current.DefaultContainer;
        _defaultDownloadFolder = current.DefaultDownloadDirectory ?? string.Empty;
        _startMinimizedToTray = current.StartMinimizedToTray;
        _closeToTray = current.CloseToTray;
        _monitorClipboard = current.MonitorClipboard;
        _suppress = false;
    }

    public IReadOnlyList<ThemeMode> Themes { get; } = [ThemeMode.Light, ThemeMode.Dark, ThemeMode.System];

    public IReadOnlyList<UiDensity> Densities { get; } = [UiDensity.Comfortable, UiDensity.Compact];

    public IReadOnlyList<VideoQuality> VideoQualities { get; } =
        [VideoQuality.P360, VideoQuality.P480, VideoQuality.P720, VideoQuality.P1080, VideoQuality.P1440, VideoQuality.P2160];

    public IReadOnlyList<MediaContainer> Containers { get; } =
        [MediaContainer.Mkv, MediaContainer.Mp4, MediaContainer.Webm];

    partial void OnSelectedThemeChanged(ThemeMode value)
    {
        if (_suppress)
        {
            return;
        }

        _theme.SetMode(value); // live
        // Persist Light/Dark; System follows the OS and has no stored AppSettings equivalent.
        if (value is ThemeMode.Light or ThemeMode.Dark)
        {
            _ = _settings.UpdateAsync(s => s with { Theme = value == ThemeMode.Dark ? AppTheme.Dark : AppTheme.Light });
        }
    }

    partial void OnDensityChanged(UiDensity value)
    {
        if (!_suppress)
        {
            _ = _settings.UpdateAsync(s => s with { Density = value });
        }
    }

    partial void OnDefaultVideoQualityChanged(VideoQuality value)
    {
        if (!_suppress)
        {
            _ = _settings.UpdateAsync(s => s with { DefaultVideoQuality = value });
        }
    }

    partial void OnDefaultContainerChanged(MediaContainer value)
    {
        if (!_suppress)
        {
            _ = _settings.UpdateAsync(s => s with { DefaultContainer = value });
        }
    }

    /// <summary>Red validation error for the default folder (invalid path characters), or null when acceptable.</summary>
    public string? DefaultDownloadFolderError =>
        !string.IsNullOrWhiteSpace(DefaultDownloadFolder)
        && DefaultDownloadFolder.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            ? "That isn't a valid folder path."
            : null;

    /// <summary>Informational hint when the chosen folder is valid but does not exist yet, or null otherwise.</summary>
    public string? DefaultDownloadFolderHint
    {
        get
        {
            string folder = DefaultDownloadFolder.Trim();
            if (folder.Length == 0 || DefaultDownloadFolderError is not null)
            {
                return null;
            }

            return Directory.Exists(folder)
                ? null
                : "This folder doesn't exist yet — it'll be created on the first download.";
        }
    }

    partial void OnDefaultDownloadFolderChanged(string value)
    {
        // Don't persist an invalid path — keep the last valid value until the user fixes it. A valid but
        // not-yet-existing folder is fine (the engine creates it on first use).
        if (_suppress || DefaultDownloadFolderError is not null)
        {
            return;
        }

        string? folder = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        _ = _settings.UpdateAsync(s => s with { DefaultDownloadDirectory = folder });
    }

    partial void OnStartMinimizedToTrayChanged(bool value)
    {
        if (!_suppress)
        {
            _ = _settings.UpdateAsync(s => s with { StartMinimizedToTray = value });
        }
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (!_suppress)
        {
            _ = _settings.UpdateAsync(s => s with { CloseToTray = value });
        }
    }

    partial void OnMonitorClipboardChanged(bool value)
    {
        if (!_suppress)
        {
            _ = _settings.UpdateAsync(s => s with { MonitorClipboard = value });
        }
    }
}
