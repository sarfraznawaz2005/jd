using JustDownload.Core.Transport.Auth;

namespace JustDownload.Core.Transport;

/// <summary>
/// Default <see cref="IResourceProbe"/> over <see cref="ITransport"/> (TASK-024). It issues a one-byte
/// ranged GET (<c>Range: bytes=0-0</c>): a <c>206 Partial Content</c> proves range support and its
/// <c>Content-Range</c> reveals the total size, while a <c>200 OK</c> means the server ignored the range
/// (no range support) and its <c>Content-Length</c> is the size. Range support is taken from the actual
/// <c>206</c>, not the advisory <c>Accept-Ranges</c> header, so a server that advertises but does not
/// honour ranges is correctly treated as single-connection. The body is never downloaded — the response
/// is disposed as soon as the headers are read.
/// </summary>
internal sealed class ResourceProbe : IResourceProbe
{
    private static readonly ByteRange FirstByte = new(0, 0);

    private readonly ITransport _transport;

    public ResourceProbe(ITransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
    }

    public async Task<ResourceProbeResult> ProbeAsync(
        Uri url,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        IReadOnlyList<KeyValuePair<string, string>> requestHeaders = headers ?? [];

        await using (ITransportResponse ranged = await _transport.SendAsync(
            new TransportRequest { Uri = url, Headers = requestHeaders, Range = FirstByte },
            cancellationToken).ConfigureAwait(false))
        {
            if (ranged.IsPartialContent)
            {
                // Range honoured: the Content-Range total is the authoritative size (may be null = "*").
                return ToResult(ranged, supportsRanges: true, totalLength: ranged.ContentRange?.TotalLength);
            }

            if (ranged.IsSuccessStatusCode)
            {
                // Range ignored (200): no usable range support; Content-Length is the full size.
                return ToResult(ranged, supportsRanges: false, totalLength: ranged.ContentLength);
            }
        }

        // The ranged probe was rejected (e.g. 416 Range Not Satisfiable on a zero-length resource).
        // Re-probe without a range so we can still learn the size and surface a real error if any.
        await using ITransportResponse plain = await _transport.SendAsync(
            new TransportRequest { Uri = url, Headers = requestHeaders },
            cancellationToken).ConfigureAwait(false);

        if (!plain.IsSuccessStatusCode)
        {
            // Surface an auth challenge distinctly so the caller can (re-)prompt for credentials (TASK-035).
            if (plain.StatusCode is 401 or 407)
            {
                throw new AuthenticationRequiredException(plain.StatusCode, isProxy: plain.StatusCode == 407);
            }

            throw new ResourceProbeException(url, plain.StatusCode);
        }

        // Without a successful range probe we can only fall back to the advertised header as a hint.
        return ToResult(plain, supportsRanges: plain.AcceptsRanges, totalLength: plain.ContentLength);
    }

    private static ResourceProbeResult ToResult(
        ITransportResponse response, bool supportsRanges, long? totalLength) => new()
        {
            FinalUri = response.FinalUri,
            StatusCode = response.StatusCode,
            SupportsRanges = supportsRanges,
            TotalLength = totalLength is >= 0 ? totalLength : null,
            SuggestedFileName = response.SuggestedFileName,
            ETag = response.ETag,
            LastModified = response.LastModified,
        };
}
