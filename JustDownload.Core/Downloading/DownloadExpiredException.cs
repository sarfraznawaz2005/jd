namespace JustDownload.Core.Downloading;

/// <summary>
/// Thrown when a download's source link has expired — the server answered with a status that conventionally
/// means a time-limited/signed URL has lapsed (<c>403</c>/<c>410</c>) (TASK-032, US-13). The manager surfaces
/// this as the Expired state and keeps the checkpoint, so a renew with a fresh URL can resume the bytes.
/// </summary>
public sealed class DownloadExpiredException : Exception
{
    public DownloadExpiredException()
        : this("The download link has expired (the server rejected the request as 403/410).")
    {
    }

    public DownloadExpiredException(string message)
        : base(message)
    {
    }

    public DownloadExpiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The HTTP status that signalled expiry, when known.</summary>
    public int StatusCode { get; init; }
}
