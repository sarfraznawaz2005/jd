using JustDownload.Core.Abstractions;
using JustDownload.Core.Data.Repositories;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Lifecycle;

/// <summary>
/// Default <see cref="IDownloadScheduler"/> (TASK-073). Start/stop are one-shot timers: it computes the
/// delay from <see cref="IClock.UtcNow"/> and, after waiting, starts or pauses the queue (firing
/// immediately for a past time). The completion action is event-driven: it watches the manager's status
/// changes and, when a download reaches a terminal/paused state and the queue has fully drained (nothing
/// running and nothing queued), runs the configured power action exactly once. All timers are cancelled on
/// dispose so none outlive the scheduler.
/// </summary>
internal sealed partial class DownloadScheduler : IDownloadScheduler
{
    private readonly object _gate = new();
    private readonly IDownloadQueueService _queue;
    private readonly IDownloadManager _manager;
    private readonly IDownloadRepository _repository;
    private readonly ISystemPowerController _power;
    private readonly IClock _clock;
    private readonly ILogger<DownloadScheduler> _logger;

    private CancellationTokenSource? _startCts;
    private CancellationTokenSource? _stopCts;
    private int _completionActionFired;
    private bool _disposed;

    public DownloadScheduler(
        IDownloadQueueService queue,
        IDownloadManager manager,
        IDownloadRepository repository,
        ISystemPowerController power,
        IClock clock,
        ILogger<DownloadScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(power);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _queue = queue;
        _manager = manager;
        _repository = repository;
        _power = power;
        _clock = clock;
        _logger = logger;
        _manager.StatusChanged += OnStatusChanged;
    }

    public DateTimeOffset? StartAt { get; private set; }

    public DateTimeOffset? StopAt { get; private set; }

    public QueueCompletionAction CompletionAction { get; set; } = QueueCompletionAction.None;

    public void ScheduleStart(DateTimeOffset at)
    {
        lock (_gate)
        {
            _startCts?.Cancel();
            _startCts?.Dispose();
            _startCts = new CancellationTokenSource();
            StartAt = at;
            // A new scheduled run is a fresh chance to power off afterwards.
            Interlocked.Exchange(ref _completionActionFired, 0);
            ArmAsync(at, isStart: true, _startCts.Token);
        }
    }

    public void ScheduleStop(DateTimeOffset at)
    {
        lock (_gate)
        {
            _stopCts?.Cancel();
            _stopCts?.Dispose();
            _stopCts = new CancellationTokenSource();
            StopAt = at;
            ArmAsync(at, isStart: false, _stopCts.Token);
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _startCts?.Cancel();
            _stopCts?.Cancel();
            StartAt = null;
            StopAt = null;
        }
    }

    private void ArmAsync(DateTimeOffset at, bool isStart, CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                TimeSpan delay = at - _clock.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (isStart)
                {
                    LogStarting(_logger);
                    await _queue.StartAsync(token).ConfigureAwait(false);
                }
                else
                {
                    LogStopping(_logger);
                    await _queue.StopAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogScheduleFailed(_logger, ex);
            }
        }, token);
    }

    private void OnStatusChanged(object? sender, DownloadStatusChangedEventArgs e)
    {
        if (CompletionAction == QueueCompletionAction.None)
        {
            return;
        }

        // Only a transition that could empty the queue is worth checking.
        if (e.Current is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Expired
            or DownloadStatus.Paused)
        {
            _ = RunCompletionActionGuardedAsync();
        }
    }

    /// <summary>
    /// Runs the completion action as a guarded fire-and-forget: any failure (e.g. the power controller
    /// throwing) is logged at error level immediately rather than becoming an unobserved task exception
    /// surfaced unpredictably at GC (no silent failures, §1).
    /// </summary>
    private async Task RunCompletionActionGuardedAsync()
    {
        try
        {
            await MaybeRunCompletionActionAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogCompletionActionFailed(_logger, ex);
        }
    }

    private async Task MaybeRunCompletionActionAsync()
    {
        QueueCompletionAction action = CompletionAction;
        if (action == QueueCompletionAction.None || _queue.RunningIds.Count > 0)
        {
            return;
        }

        IReadOnlyList<Data.Models.Download> queued = await _repository
            .GetByStatusOrderedByPriorityAsync(DownloadStatusCodes.Queued)
            .ConfigureAwait(false);
        if (queued.Count > 0 || _queue.RunningIds.Count > 0)
        {
            return; // still work to do
        }

        // Fire the power action at most once per scheduled session.
        if (Interlocked.CompareExchange(ref _completionActionFired, 1, 0) != 0)
        {
            return;
        }

        LogCompletionAction(_logger, action);
        if (action == QueueCompletionAction.Shutdown)
        {
            await _power.ShutdownAsync().ConfigureAwait(false);
        }
        else if (action == QueueCompletionAction.Sleep)
        {
            await _power.SleepAsync().ConfigureAwait(false);
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
        lock (_gate)
        {
            _startCts?.Cancel();
            _startCts?.Dispose();
            _stopCts?.Cancel();
            _stopCts?.Dispose();
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Scheduled start reached; starting the queue.")]
    private static partial void LogStarting(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Scheduled stop reached; pausing the queue.")]
    private static partial void LogStopping(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Queue drained; running completion action {Action}.")]
    private static partial void LogCompletionAction(ILogger logger, QueueCompletionAction action);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "A scheduled queue action failed.")]
    private static partial void LogScheduleFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "The queue completion action failed.")]
    private static partial void LogCompletionActionFailed(ILogger logger, Exception exception);
}
