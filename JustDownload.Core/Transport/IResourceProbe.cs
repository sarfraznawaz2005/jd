namespace JustDownload.Core.Transport;

/// <summary>
/// Probes a URL before downloading to learn whether the server supports byte ranges and how large the
/// resource is (TASK-024). The download engine uses the result to choose between a segmented, multi-
/// connection download and the single-connection fallback (US-1 AC3).
/// </summary>
public interface IResourceProbe
{
    /// <summary>
    /// Probes <paramref name="url"/> and returns its capabilities. Follows redirects (the result's
    /// <see cref="ResourceProbeResult.FinalUri"/> is the resolved URL).
    /// </summary>
    /// <param name="url">The URL to probe.</param>
    /// <param name="headers">Optional extra request headers (e.g. cookies/referrer from the extension).</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <exception cref="ResourceProbeException">The server returned an error for the resource.</exception>
    Task<ResourceProbeResult> ProbeAsync(
        Uri url,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null,
        CancellationToken cancellationToken = default);
}
