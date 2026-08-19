using JustDownload.Core.Transport.Auth;

namespace JustDownload.Core.Transport;

/// <summary>
/// Default <see cref="IResourceProbe"/> over <see cref="ITransport"/> (TASK-024). It issues a one-byte
/// ranged GET (<c>Range: bytes=0-0</c>): a <c>206 Partial Content</c> proves range support and its
/// <c>Content-Range</c> reveals the total size, while a <c>200 OK</c> means the server ignored the range
/// (no range support) and its <c>Content-Length</c> is the size. Range support is taken from the actual
/// <c>206</c>, not the advisory <c>Accept-Ranges</c> header, so a server that advertises but does not
/// honour ranges is correctly treated as single-connection.
/// <para>
/// A <c>200</c> means the server is already streaming the entire resource at us, so
/// <see cref="OpenAsync"/> keeps that response open for the caller to consume as the download (TASK-262)
/// — the body is only thrown away by <see cref="ProbeAsync"/>, whose callers just want the metadata.
/// </para>
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
        await using ProbedResource probed =
            await OpenAsync(url, headers, cancellationToken).ConfigureAwait(false);
        return probed.Result;
    }

    public async Task<ProbedResource> OpenAsync(
        Uri url,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        IReadOnlyList<KeyValuePair<string, string>> requestHeaders = headers ?? [];

        ITransportResponse ranged = await _transport.SendAsync(
            new TransportRequest { Uri = url, Headers = requestHeaders, Range = FirstByte },
            cancellationToken).ConfigureAwait(false);

        bool keepRanged = false;
        try
        {
            if (ranged.IsPartialContent)
            {
                // Range honoured: the Content-Range total is the authoritative size (may be null = "*").
                // The body is a single byte, not the resource, so nothing is worth keeping open.
                return new ProbedResource(
                    ToResult(ranged, supportsRanges: true, totalLength: ranged.ContentRange?.TotalLength),
                    body: null);
            }

            if (ranged.IsSuccessStatusCode)
            {
                // Range ignored (200): no usable range support; Content-Length is the size, and the body
                // now arriving is the complete resource — hand it over rather than asking again.
                keepRanged = true;
                return new ProbedResource(
                    ToResult(ranged, supportsRanges: false, totalLength: ranged.ContentLength),
                    body: ranged);
            }
        }
        finally
        {
            if (!keepRanged)
            {
                await ranged.DisposeAsync().ConfigureAwait(false);
            }
        }

        // The ranged probe was rejected (e.g. 416 Range Not Satisfiable on a zero-length resource).
        // Re-probe without a range so we can still learn the size and surface a real error if any.
        ITransportResponse plain = await _transport.SendAsync(
            new TransportRequest { Uri = url, Headers = requestHeaders },
            cancellationToken).ConfigureAwait(false);

        bool keepPlain = false;
        try
        {
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
            // This body is an un-ranged GET, so it too is the complete resource.
            keepPlain = true;
            return new ProbedResource(
                ToResult(plain, supportsRanges: plain.AcceptsRanges, totalLength: plain.ContentLength),
                body: plain);
        }
        finally
        {
            if (!keepPlain)
            {
                await plain.DisposeAsync().ConfigureAwait(false);
            }
        }
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
