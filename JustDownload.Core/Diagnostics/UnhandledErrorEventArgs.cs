namespace JustDownload.Core.Diagnostics;

/// <summary>
/// Carries an unhandled error from <see cref="IGlobalErrorHandler"/> to its subscribers (typically
/// the UI, which turns it into a user-visible error state). This is the "surface" half of
/// CLAUDE.md §1's "no silent failures" rule.
/// </summary>
public sealed class UnhandledErrorEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="UnhandledErrorEventArgs"/> class.</summary>
    /// <param name="exception">The exception that went unhandled.</param>
    /// <param name="source">Where it was caught (e.g. <c>AppDomain.UnhandledException</c>).</param>
    /// <param name="isTerminating">Whether the runtime is tearing the process down as a result.</param>
    public UnhandledErrorEventArgs(Exception exception, string source, bool isTerminating)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(source);

        Exception = exception;
        Source = source;
        IsTerminating = isTerminating;
    }

    /// <summary>The exception that went unhandled.</summary>
    public Exception Exception { get; }

    /// <summary>A short description of where the error was captured.</summary>
    public string Source { get; }

    /// <summary>True when the runtime is terminating the process because of this error.</summary>
    public bool IsTerminating { get; }
}
