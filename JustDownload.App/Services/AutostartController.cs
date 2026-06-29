using JustDownload.Core;
using JustDownload.Core.Settings;

namespace JustDownload.App.Services;

/// <summary>
/// Keeps the OS launch-at-login registration in sync with <see cref="AppSettings.LaunchAtStartup"/>
/// (TASK-122) — the setting is the source of truth, reconciled to the OS on startup and on every change, so
/// toggling it takes effect without a restart. A no-op where autostart is unsupported, and in portable mode
/// (TASK-138) where the app must never write to the registry. Mirrors the speed-limit / proxy controllers.
/// </summary>
public sealed class AutostartController : IDisposable
{
    private readonly IAutostartService _autostart;
    private readonly ISettingsService _settings;
    private readonly IPortableEnvironment _portable;

    public AutostartController(IAutostartService autostart, ISettingsService settings, IPortableEnvironment portable)
    {
        ArgumentNullException.ThrowIfNull(autostart);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(portable);
        _autostart = autostart;
        _settings = settings;
        _portable = portable;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Reconciles the OS autostart registration with the loaded setting. Call once after load.</summary>
    public void ApplyCurrent() => Apply(_settings.Current.LaunchAtStartup);

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) => Apply(e.Current.LaunchAtStartup);

    private void Apply(bool enabled)
    {
        // Portable mode must leave no trace on the host — never touch the registry Run key (TASK-138).
        if (_portable.IsPortable)
        {
            return;
        }

        if (_autostart.IsSupported)
        {
            _autostart.SetEnabled(enabled);
        }
    }

    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}
