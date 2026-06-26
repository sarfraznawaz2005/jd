namespace JustDownload.Core.Diagnostics;

/// <summary>
/// The single funnel for unhandled errors (CLAUDE.md §1: "every error is surfaced through the global
/// error handler … a swallowed catch {} is a bug"). It logs the failure (loud, redacted) and raises
/// <see cref="ErrorOccurred"/> so a host can surface it to the user instead of failing silently.
/// </summary>
public interface IGlobalErrorHandler
{
    /// <summary>
    /// Raised for every captured unhandled error. Subscribers (e.g. the UI) should present it; a
    /// throwing subscriber is itself logged and never allowed to mask the original error.
    /// </summary>
    event EventHandler<UnhandledErrorEventArgs>? ErrorOccurred;

    /// <summary>
    /// Hooks the process-wide unhandled-exception sources
    /// (<see cref="AppDomain.UnhandledException"/> and
    /// <see cref="System.Threading.Tasks.TaskScheduler.UnobservedTaskException"/>). Idempotent — a
    /// second call is a no-op. A host calls this once at startup.
    /// </summary>
    void Install();

    /// <summary>
    /// Reports an exception caught at an explicit call site (e.g. a top-level UI dispatcher hook or a
    /// background worker boundary). Logs it and raises <see cref="ErrorOccurred"/>.
    /// </summary>
    /// <param name="exception">The exception to surface.</param>
    /// <param name="source">A short description of the catch site.</param>
    void Handle(Exception exception, string source);
}
