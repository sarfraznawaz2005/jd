namespace JustDownload.Core.Media;

/// <summary>Raised when Deno cannot be downloaded, integrity-verified, or located/run afterwards.</summary>
public sealed class DenoException : Exception
{
    public DenoException()
    {
    }

    public DenoException(string message)
        : base(message)
    {
    }

    public DenoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
