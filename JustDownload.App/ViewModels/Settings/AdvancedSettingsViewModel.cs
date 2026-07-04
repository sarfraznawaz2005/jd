using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustDownload.App.Services;
using JustDownload.Core.Logging;
using JustDownload.Core.Settings;
using Microsoft.Extensions.Logging;

namespace JustDownload.App.ViewModels.Settings;

/// <summary>
/// Advanced settings (TASK-127): real, persisted options for power users — the minimum log level (applied
/// live through the logging switch), the post-download command, and a way to actually view the error log
/// <see cref="IGlobalErrorHandler"/> writes to (TASK-179 previously had no UI for it at all). Persists through
/// the <see cref="ISettingsService"/>; a suppress flag stops the initial hydration from writing back. The
/// privacy notes describe the (non-configurable) no-telemetry / log-redaction guarantees honestly.
/// </summary>
public sealed partial class AdvancedSettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IErrorLogPathProvider _errorLogPath;
    private readonly IFileRevealer _fileRevealer;
    private bool _suppress;

    [ObservableProperty]
    private LogLevel _logLevel;

    [ObservableProperty]
    private string _onCompletionCommand;

    public AdvancedSettingsViewModel(
        ISettingsService settings, IErrorLogPathProvider errorLogPath, IFileRevealer fileRevealer)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(errorLogPath);
        ArgumentNullException.ThrowIfNull(fileRevealer);
        _settings = settings;
        _errorLogPath = errorLogPath;
        _fileRevealer = fileRevealer;

        _suppress = true;
        _logLevel = settings.Current.MinimumLogLevel;
        _onCompletionCommand = settings.Current.OnCompletionCommand ?? string.Empty;
        _suppress = false;
    }

    /// <summary>Opens the error log if anything has been logged, otherwise reveals where it will appear.</summary>
    [RelayCommand]
    private void ViewErrorLogs()
    {
        string path = _errorLogPath.FilePath;
        if (File.Exists(path))
        {
            _fileRevealer.OpenFile(path);
        }
        else
        {
            _fileRevealer.RevealInFolder(path);
        }
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
