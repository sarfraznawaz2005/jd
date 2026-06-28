namespace JustDownload.Core.Lifecycle;

/// <summary>
/// Schedules the download queue to start and stop at set times and, optionally, to power the machine down
/// or to sleep once the queue finishes (TASK-073, US-16). At the start time it starts the queue
/// (<see cref="IDownloadQueueService.StartAsync"/>); at the stop time it pauses it
/// (<see cref="IDownloadQueueService.StopAsync"/>). When <see cref="CompletionAction"/> is set and the queue
/// drains (nothing running and nothing queued), the chosen power action runs once.
/// </summary>
public interface IDownloadScheduler : IDisposable
{
    /// <summary>The scheduled start time, or <see langword="null"/> if none is set.</summary>
    DateTimeOffset? StartAt { get; }

    /// <summary>The scheduled stop time, or <see langword="null"/> if none is set.</summary>
    DateTimeOffset? StopAt { get; }

    /// <summary>The action to take when the queue finishes; <see cref="QueueCompletionAction.None"/> by default.</summary>
    QueueCompletionAction CompletionAction { get; set; }

    /// <summary>Schedules the queue to start at <paramref name="at"/> (immediately if it is in the past).</summary>
    void ScheduleStart(DateTimeOffset at);

    /// <summary>Schedules the queue to pause at <paramref name="at"/> (immediately if it is in the past).</summary>
    void ScheduleStop(DateTimeOffset at);

    /// <summary>Cancels any pending start/stop timers (does not stop a running queue).</summary>
    void Cancel();
}
