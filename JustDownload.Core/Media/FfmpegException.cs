namespace JustDownload.Core.Media;

/// <summary>Raised when ffmpeg cannot be located, started, or completes with a failing exit code (TASK-040).</summary>
public sealed class FfmpegException : Exception
{
    public FfmpegException()
    {
    }

    public FfmpegException(string message)
        : base(message)
    {
    }

    public FfmpegException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
