namespace JustDownload.Core.Transport;

/// <summary>
/// Thrown when a URL cannot be probed because the server returned an error to both the ranged probe and
/// the plain fallback request (TASK-024) — there is no usable resource to download. Carries the failing
/// URL and the last status code so the engine can surface a clear failure.
/// </summary>
public sealed class ResourceProbeException : Exception
{
    public ResourceProbeException(Uri url, int statusCode)
        : base($"Failed to probe '{url}': server responded with status {statusCode}.")
    {
        Url = url;
        StatusCode = statusCode;
    }

    public ResourceProbeException(string message)
        : base(message)
    {
    }

    public ResourceProbeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ResourceProbeException()
    {
    }

    /// <summary>The URL that could not be probed.</summary>
    public Uri? Url { get; }

    /// <summary>The last HTTP status code observed, or 0 when not applicable.</summary>
    public int StatusCode { get; }
}
