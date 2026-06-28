namespace JustDownload.Core.Lifecycle;

/// <summary>
/// The post-download power action the scheduler may take when the queue finishes (TASK-073, US-16 AC2).
/// </summary>
public enum QueueCompletionAction
{
    /// <summary>Do nothing when the queue empties (the default).</summary>
    None = 0,

    /// <summary>Shut the computer down once all downloads complete.</summary>
    Shutdown = 1,

    /// <summary>Put the computer to sleep once all downloads complete.</summary>
    Sleep = 2,
}

/// <summary>
/// Performs a system power action (TASK-073, US-16). Only ever invoked when the user explicitly opts in via
/// the scheduler's <see cref="QueueCompletionAction"/>, never automatically. The default implementation
/// shells out to the per-OS command; tests substitute a fake so no machine is ever powered off.
/// </summary>
public interface ISystemPowerController
{
    /// <summary>Requests a system shutdown.</summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests the system to sleep/suspend.</summary>
    Task SleepAsync(CancellationToken cancellationToken = default);
}
