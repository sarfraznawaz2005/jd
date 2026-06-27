namespace JustDownload.Core.Downloading;

/// <summary>
/// Thrown when a download cannot continue from a non-zero offset because the server no longer honors
/// <c>Range</c> requests — it answered a ranged request with a full <c>200</c> body instead of <c>206</c>
/// (TASK-029, US-2 AC3-4). The already-downloaded bytes are unusable for a resume, so the download must be
/// restarted from the beginning; the manager surfaces this as a restart-required outcome and clears the
/// stale checkpoint rather than corrupting the file.
/// </summary>
public sealed class ResumeNotSupportedException : Exception
{
    public ResumeNotSupportedException()
        : this("The server no longer supports resuming this download from an offset; it must be restarted.")
    {
    }

    public ResumeNotSupportedException(string message)
        : base(message)
    {
    }

    public ResumeNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
