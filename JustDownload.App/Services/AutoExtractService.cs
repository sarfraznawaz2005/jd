using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.PostProcess;
using JustDownload.Core.Settings;
using Microsoft.Extensions.Logging;

namespace JustDownload.App.Services;

/// <summary>
/// Auto-extracts a completed archive download into a sibling folder when the user opts in
/// (<see cref="AppSettings.AutoExtractArchives"/>, TASK-135). Listens for terminal Completed transitions,
/// resolves the file, and — if it is a supported archive — extracts it on a background task. Failures are
/// logged, never thrown. Start it once at app startup; dispose unsubscribes.
/// </summary>
public sealed partial class AutoExtractService : IDisposable
{
    private readonly IDownloadManager _manager;
    private readonly IDownloadRepository _repository;
    private readonly IArchiveExtractor _extractor;
    private readonly ISettingsService _settings;
    private readonly ILogger<AutoExtractService> _logger;
    private bool _disposed;

    public AutoExtractService(
        IDownloadManager manager,
        IDownloadRepository repository,
        IArchiveExtractor extractor,
        ISettingsService settings,
        ILogger<AutoExtractService> logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _manager = manager;
        _repository = repository;
        _extractor = extractor;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Begins listening for completed downloads.</summary>
    public void Start() => _manager.StatusChanged += OnStatusChanged;

    private void OnStatusChanged(object? sender, DownloadStatusChangedEventArgs e)
    {
        if (e.Current == DownloadStatus.Completed && _settings.Current.AutoExtractArchives)
        {
            _ = ExtractAsync(e.DownloadId);
        }
    }

    private async Task ExtractAsync(long id)
    {
        try
        {
            Download? record = await _repository.GetAsync(id).ConfigureAwait(false);
            if (record?.Directory is null || record.Filename is null)
            {
                return;
            }

            string path = Path.Combine(record.Directory, record.Filename);
            if (!_extractor.CanExtract(path))
            {
                return; // not an archive we handle (the common case) — nothing to do
            }

            string destination = await _extractor.ExtractAsync(path).ConfigureAwait(false);
            LogExtracted(_logger, record.Filename, destination);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogExtractFailed(_logger, id, ex);
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Auto-extracted {Archive} to {Destination}.")]
    private static partial void LogExtracted(ILogger logger, string archive, string destination);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Auto-extract failed for download {Id}.")]
    private static partial void LogExtractFailed(ILogger logger, long id, Exception exception);
}
