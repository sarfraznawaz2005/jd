using JustDownload.Core.Logging;
using JustDownload.Core.Settings;

namespace JustDownload.App.Services;

/// <summary>
/// Keeps the live logging verbosity (<see cref="ILogLevelSwitch"/>) in sync with
/// <see cref="AppSettings.MinimumLogLevel"/> (TASK-127) — reconciled on startup and on every change, so
/// adjusting the Advanced log level takes effect immediately without a restart. Mirrors the speed-limit /
/// proxy / autostart controllers.
/// </summary>
public sealed class LogLevelController : IDisposable
{
    private readonly ILogLevelSwitch _levelSwitch;
    private readonly ISettingsService _settings;

    public LogLevelController(ILogLevelSwitch levelSwitch, ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(levelSwitch);
        ArgumentNullException.ThrowIfNull(settings);
        _levelSwitch = levelSwitch;
        _settings = settings;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Applies the loaded minimum level to the live switch. Call once after settings load.</summary>
    public void ApplyCurrent() => _levelSwitch.Minimum = _settings.Current.MinimumLogLevel;

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) =>
        _levelSwitch.Minimum = e.Current.MinimumLogLevel;

    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}
