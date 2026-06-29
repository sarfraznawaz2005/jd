using JustDownload.Core.Settings;

namespace JustDownload.App.Services;

/// <summary>
/// Keeps the OS launch-at-login registration in sync with <see cref="AppSettings.LaunchAtStartup"/>
/// (TASK-122) — the setting is the source of truth, reconciled to the OS on startup and on every change, so
/// toggling it takes effect without a restart. A no-op where autostart is unsupported. Mirrors the
/// speed-limit / proxy controllers.
/// </summary>
public sealed class AutostartController : IDisposable
{
    private readonly IAutostartService _autostart;
    private readonly ISettingsService _settings;

    public AutostartController(IAutostartService autostart, ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(autostart);
        ArgumentNullException.ThrowIfNull(settings);
        _autostart = autostart;
        _settings = settings;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Reconciles the OS autostart registration with the loaded setting. Call once after load.</summary>
    public void ApplyCurrent() => Apply(_settings.Current.LaunchAtStartup);

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) => Apply(e.Current.LaunchAtStartup);

    private void Apply(bool enabled)
    {
        if (_autostart.IsSupported)
        {
            _autostart.SetEnabled(enabled);
        }
    }

    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}
