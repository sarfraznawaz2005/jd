using System.Collections.Concurrent;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Downloading;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Lifecycle;

/// <summary>
/// Default <see cref="IDownloadManager"/> (TASK-031). Persists the lifecycle of each download through the
/// <see cref="IDownloadRepository"/>, drives transfers through the <see cref="ISegmentedDownloader"/>, and
/// turns the downloader's raw byte-count progress into <see cref="DownloadProgress"/> snapshots (speed via a
/// <see cref="SpeedEstimator"/>, ETA derived from the total). Every state change is validated against
/// <see cref="DownloadStateMachine"/>, persisted, and surfaced through <see cref="StatusChanged"/> /
/// <see cref="ProgressChanged"/> so the UI never has to poll.
/// </summary>
internal sealed partial class DownloadManager : IDownloadManager
{
    private readonly IDownloadRepository _repository;
    private readonly ISegmentedDownloader _downloader;
    private readonly IClock _clock;
    private readonly ILogger<DownloadManager> _logger;
    private readonly ConcurrentDictionary<long, DownloadProgress> _latest = new();

    public DownloadManager(
        IDownloadRepository repository,
        ISegmentedDownloader downloader,
        IClock clock,
        ILogger<DownloadManager> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _downloader = downloader;
        _clock = clock;
        _logger = logger;
    }

    public event EventHandler<DownloadStatusChangedEventArgs>? StatusChanged;

    public event EventHandler<DownloadProgressChangedEventArgs>? ProgressChanged;

    public DownloadProgress? GetProgress(long id) => _latest.GetValueOrDefault(id);

    public async Task<long> EnqueueAsync(
        EnqueueDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.DestinationDirectory);
        ArgumentException.ThrowIfNullOrEmpty(request.FileName);

        var record = new Download
        {
            Url = request.Url.ToString(),
            Referrer = request.Referrer,
            Filename = request.FileName,
            Directory = request.DestinationDirectory,
            TotalBytes = request.TotalBytes,
            Status = DownloadStatusCodes.Queued,
            CategoryType = request.CategoryType,
            CategoryStatus = "Incomplete",
            CreatedAt = _clock.UtcNow,
            MaxConnections = request.MaxConnections,
            SpeedLimit = request.SpeedLimit,
        };

        long id = await _repository.AddAsync(record, cancellationToken).ConfigureAwait(false);
        LogEnqueued(_logger, id, record.Url);
        RaiseStatus(id, previous: null, DownloadStatus.Queued);
        return id;
    }

    public async Task<DownloadResult> StartAsync(long id, CancellationToken cancellationToken = default)
    {
        Download record = await _repository.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No download exists with id {id}.");

        DownloadStatus from = DownloadStatusCodes.Parse(record.Status);
        DownloadStateMachine.EnsureCanTransition(from, DownloadStatus.Active);

        if (string.IsNullOrEmpty(record.Directory) || string.IsNullOrEmpty(record.Filename))
        {
            throw new InvalidOperationException(
                $"Download {id} has no destination path resolved and cannot be started.");
        }

        Download active = record with { Status = DownloadStatusCodes.Active, Error = null };
        await _repository.UpdateAsync(active, cancellationToken).ConfigureAwait(false);
        RaiseStatus(id, from, DownloadStatus.Active);

        var estimator = new SpeedEstimator();
        bool resumable = record.TotalBytes is > 0;
        var sink = new ProgressSink(this, id, estimator, record.TotalBytes, resumable);

        var downloadRequest = new DownloadRequest
        {
            Url = new Uri(record.Url),
            DestinationPath = Path.Combine(record.Directory, record.Filename),
            Connections = record.MaxConnections,
            SpeedLimit = record.SpeedLimit,
            Headers = record.Referrer is { Length: > 0 } referrer
                ? [new KeyValuePair<string, string>("Referer", referrer)]
                : [],
        };

        DownloadResult result;
        try
        {
            result = await _downloader.DownloadAsync(downloadRequest, sink, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a pause: the partial file and segment checkpoints remain for resume.
            await TransitionToTerminalAsync(id, active, DownloadStatus.Paused, error: null, completedAt: null)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await TransitionToTerminalAsync(id, active, DownloadStatus.Failed, ex.Message, completedAt: null)
                .ConfigureAwait(false);
            LogFailed(_logger, id, ex);
            throw;
        }

        await TransitionToTerminalAsync(
            id,
            active with { TotalBytes = result.TotalBytes },
            DownloadStatus.Completed,
            error: null,
            completedAt: _clock.UtcNow).ConfigureAwait(false);

        // Final snapshot: 100%, ETA zero, resumable iff the transfer used ranges.
        DownloadProgress done = DownloadProgress.Create(
            DownloadStatus.Completed, result.TotalBytes, result.TotalBytes, 0, !result.SingleConnection);
        _latest[id] = done;
        ProgressChanged?.Invoke(this, new DownloadProgressChangedEventArgs(id, done));

        LogCompleted(_logger, id, result.TotalBytes);
        return result;
    }

    private async Task TransitionToTerminalAsync(
        long id,
        Download current,
        DownloadStatus to,
        string? error,
        DateTimeOffset? completedAt)
    {
        DownloadStatus fromStatus = DownloadStatusCodes.Parse(current.Status);
        DownloadStateMachine.EnsureCanTransition(fromStatus, to);

        Download updated = current with
        {
            Status = DownloadStatusCodes.ToCode(to),
            Error = error,
            CompletedAt = completedAt,
            CategoryStatus = to == DownloadStatus.Completed ? "Complete" : current.CategoryStatus,
        };

        // Persist on the same token-free path even when the caller's token was cancelled (a pause must
        // still record the paused state).
        await _repository.UpdateAsync(updated, CancellationToken.None).ConfigureAwait(false);
        RaiseStatus(id, fromStatus, to);
    }

    private void RaiseStatus(long id, DownloadStatus? previous, DownloadStatus current) =>
        StatusChanged?.Invoke(this, new DownloadStatusChangedEventArgs(id, previous, current));

    private void OnBytes(long id, SpeedEstimator estimator, long? total, bool resumable, long cumulativeBytes)
    {
        double speed = estimator.Sample(_clock.UtcNow, cumulativeBytes);
        DownloadProgress snapshot = DownloadProgress.Create(
            DownloadStatus.Active, cumulativeBytes, total, speed, resumable);
        _latest[id] = snapshot;
        ProgressChanged?.Invoke(this, new DownloadProgressChangedEventArgs(id, snapshot));
    }

    /// <summary>
    /// A synchronous <see cref="IProgress{T}"/> bridge: the segmented downloader reports cumulative bytes
    /// from its worker threads, and each report is turned into a progress snapshot. Using a direct
    /// implementation (rather than <see cref="Progress{T}"/>) keeps the callback on the reporting thread so
    /// the speed estimator sees samples in close to real order.
    /// </summary>
    private sealed class ProgressSink : IProgress<long>
    {
        private readonly DownloadManager _owner;
        private readonly long _id;
        private readonly SpeedEstimator _estimator;
        private readonly long? _total;
        private readonly bool _resumable;

        public ProgressSink(DownloadManager owner, long id, SpeedEstimator estimator, long? total, bool resumable)
        {
            _owner = owner;
            _id = id;
            _estimator = estimator;
            _total = total;
            _resumable = resumable;
        }

        public void Report(long value) => _owner.OnBytes(_id, _estimator, _total, _resumable, value);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Enqueued download {Id}: {Url}.")]
    private static partial void LogEnqueued(ILogger logger, long id, string url);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Download {Id} completed ({Bytes} bytes).")]
    private static partial void LogCompleted(ILogger logger, long id, long bytes);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Download {Id} failed.")]
    private static partial void LogFailed(ILogger logger, long id, Exception exception);
}
