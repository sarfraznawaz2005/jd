using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Settings;
using Microsoft.Extensions.Logging;

namespace JustDownload.App.Services;

/// <summary>
/// Runs a user-configured program when a download completes (TASK-136) — for move/scan/extract/upload
/// workflows. The completed file's full path is passed as a single argument via <see cref="IProcessLauncher"/>
/// (no shell), so the path is passed safely with no injection. Opt-in: nothing runs unless
/// <see cref="AppSettings.OnCompletionCommand"/> is set. Failures are logged, never thrown. Start it once at
/// app startup; dispose unsubscribes.
/// </summary>
public sealed partial class PostDownloadCommandService : IDisposable
{
    private readonly IDownloadManager _manager;
    private readonly IDownloadRepository _repository;
    private readonly ISettingsService _settings;
    private readonly IProcessLauncher _launcher;
    private readonly ILogger<PostDownloadCommandService> _logger;
    private bool _disposed;

    public PostDownloadCommandService(
        IDownloadManager manager,
        IDownloadRepository repository,
        ISettingsService settings,
        IProcessLauncher launcher,
        ILogger<PostDownloadCommandService> logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(logger);
        _manager = manager;
        _repository = repository;
        _settings = settings;
        _launcher = launcher;
        _logger = logger;
    }

    /// <summary>Begins listening for completed downloads.</summary>
    public void Start() => _manager.StatusChanged += OnStatusChanged;

    private void OnStatusChanged(object? sender, DownloadStatusChangedEventArgs e)
    {
        if (e.Current != DownloadStatus.Completed)
        {
            return;
        }

        string? command = _settings.Current.OnCompletionCommand;
        if (!string.IsNullOrWhiteSpace(command))
        {
            _ = RunAsync(e.DownloadId, command.Trim());
        }
    }

    private async Task RunAsync(long id, string command)
    {
        try
        {
            Download? record = await _repository.GetAsync(id).ConfigureAwait(false);
            if (record?.Directory is null || record.Filename is null)
            {
                return;
            }

            string filePath = Path.Combine(record.Directory, record.Filename);
            _launcher.Launch(command, [filePath]);
            LogRan(_logger, command, record.Filename);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(_logger, command, ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _manager.StatusChanged -= OnStatusChanged;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Ran completion command {Command} for {File}.")]
    private static partial void LogRan(ILogger logger, string command, string file);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Completion command {Command} failed to launch.")]
    private static partial void LogFailed(ILogger logger, string command, Exception exception);
}
