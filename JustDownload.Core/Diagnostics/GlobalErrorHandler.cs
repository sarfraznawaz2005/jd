using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Diagnostics;

/// <summary>
/// Default <see cref="IGlobalErrorHandler"/>. Captures process-wide unhandled exceptions, logs them
/// at <see cref="LogLevel.Critical"/> through the redacting logger pipeline, and re-raises them as
/// <see cref="ErrorOccurred"/> events for the UI to present. Nothing is swallowed (CLAUDE.md §1).
/// </summary>
internal sealed partial class GlobalErrorHandler : IGlobalErrorHandler, IDisposable
{
    private readonly ILogger<GlobalErrorHandler> _logger;

    // 0 = not installed, 1 = installed. Guards against double-subscribing / double-removing.
    private int _installed;

    public GlobalErrorHandler(ILogger<GlobalErrorHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public event EventHandler<UnhandledErrorEventArgs>? ErrorOccurred;

    public void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    public void Handle(Exception exception, string source)
        => Capture(exception, source, isTerminating: false);

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Exception exception = e.ExceptionObject as Exception
            ?? new InvalidOperationException(
                $"Non-Exception object thrown: {e.ExceptionObject?.GetType().FullName ?? "null"}");

        Capture(exception, "AppDomain.UnhandledException", e.IsTerminating);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Capture(e.Exception, "TaskScheduler.UnobservedTaskException", isTerminating: false);

        // We have surfaced it; mark observed so it does not (on older runtime policies) escalate.
        e.SetObserved();
    }

    private void Capture(Exception exception, string source, bool isTerminating)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Loud and never silent. The message template flows through the redacting logger, so any
        // secret embedded in the text is masked before it hits a sink (CLAUDE.md §5).
        LogUnhandled(source, isTerminating, exception);

        EventHandler<UnhandledErrorEventArgs>? handlers = ErrorOccurred;
        if (handlers is null)
        {
            return;
        }

        try
        {
            handlers.Invoke(this, new UnhandledErrorEventArgs(exception, source, isTerminating));
        }
        catch (Exception subscriberFailure)
        {
            // A faulty subscriber must not mask the original error — log it and move on.
            LogSubscriberFailure(source, subscriberFailure);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Critical,
        Message = "Unhandled exception from {Source} (terminating: {IsTerminating})")]
    private partial void LogUnhandled(string source, bool isTerminating, Exception exception);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "An ErrorOccurred subscriber threw while surfacing an unhandled exception from {Source}")]
    private partial void LogSubscriberFailure(string source, Exception exception);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _installed, 0) == 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }
}
