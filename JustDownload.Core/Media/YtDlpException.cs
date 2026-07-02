namespace JustDownload.Core.Media;

/// <summary>Raised when yt-dlp cannot be downloaded, integrity-verified, or located/run afterwards (TASK-162).</summary>
public sealed class YtDlpException : Exception
{
    public YtDlpException()
    {
    }

    public YtDlpException(string message)
        : base(message)
    {
    }

    public YtDlpException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
