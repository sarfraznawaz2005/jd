using CommunityToolkit.Mvvm.ComponentModel;
using JustDownload.Core.Settings;
using Microsoft.Extensions.Logging;

namespace JustDownload.App.ViewModels.Settings;

/// <summary>
/// Advanced settings (TASK-127): real, persisted options for power users — currently the minimum log level,
/// applied live through the logging switch. Persists through the <see cref="ISettingsService"/>; a suppress
/// flag stops the initial hydration from writing back. The privacy notes describe the (non-configurable)
/// no-telemetry / log-redaction guarantees honestly.
/// </summary>
public sealed partial class AdvancedSettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private bool _suppress;

    [ObservableProperty]
    private LogLevel _logLevel;

    [ObservableProperty]
    private string _onCompletionCommand;

    public AdvancedSettingsViewModel(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;

        _suppress = true;
        _logLevel = settings.Current.MinimumLogLevel;
        _onCompletionCommand = settings.Current.OnCompletionCommand ?? string.Empty;
        _suppress = false;
    }

    /// <summary>The selectable log levels, least-to-most verbose order reversed for a natural menu.</summary>
    public IReadOnlyList<LogLevel> LogLevels { get; } =
        [LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error];

    partial void OnLogLevelChanged(LogLevel value)
    {
        if (!_suppress)
        {
            _ = _settings.UpdateAsync(s => s with { MinimumLogLevel = value });
        }
    }

    partial void OnOnCompletionCommandChanged(string value)
    {
        if (!_suppress)
        {
            string? command = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            _ = _settings.UpdateAsync(s => s with { OnCompletionCommand = command });
        }
    }
}
