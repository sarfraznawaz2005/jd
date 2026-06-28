using JustDownload.Core.Settings;

namespace JustDownload.App.Services;

/// <summary>
/// Applies and toggles the list/detail density (TASK-063, PRD §2.4.4). Density is a persisted preference, so
/// this is a thin façade over <see cref="ISettingsService"/>: the current value always reflects the latest
/// saved settings, <see cref="Toggle"/> flips Comfortable ⇄ Compact and persists it, and <see cref="Changed"/>
/// fires whenever the density changes (including a change made elsewhere, e.g. the settings screen) so the
/// shell can re-apply the compact style class. Mirrors <see cref="IThemeService"/>.
/// </summary>
public interface IDensityService
{
    /// <summary>The current density (always the latest persisted value).</summary>
    UiDensity Density { get; }

    /// <summary>Whether the current density is <see cref="UiDensity.Compact"/>.</summary>
    bool IsCompact { get; }

    /// <summary>Raised after the density changes.</summary>
    event EventHandler? Changed;

    /// <summary>Sets the density and persists it.</summary>
    void SetDensity(UiDensity density);

    /// <summary>Flips between Comfortable and Compact and persists it.</summary>
    void Toggle();
}

/// <summary>
/// Default <see cref="IDensityService"/>. Reads/writes <see cref="ISettingsService"/> so the toggle and the
/// settings-screen dropdown stay in lock-step, and re-raises <see cref="Changed"/> when the persisted
/// density changes.
/// </summary>
public sealed class DensityService : IDensityService
{
    private readonly ISettingsService _settings;
    private UiDensity _last;

    public DensityService(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _last = settings.Current.Density;
        _settings.Changed += OnSettingsChanged;
    }

    public event EventHandler? Changed;

    public UiDensity Density => _settings.Current.Density;

    public bool IsCompact => Density == UiDensity.Compact;

    public void SetDensity(UiDensity density)
    {
        if (density != _settings.Current.Density)
        {
            _ = _settings.UpdateAsync(s => s with { Density = density });
        }
    }

    public void Toggle() =>
        SetDensity(IsCompact ? UiDensity.Comfortable : UiDensity.Compact);

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        UiDensity current = _settings.Current.Density;
        if (current != _last)
        {
            _last = current;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
