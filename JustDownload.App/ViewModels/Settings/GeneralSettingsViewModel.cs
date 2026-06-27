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
}
