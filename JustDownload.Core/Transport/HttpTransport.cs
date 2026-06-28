using System.Net.Http;
using System.Net.Http.Headers;
using JustDownload.Core.Transport.Auth;
using JustDownload.Core.Transport.Proxy;

namespace JustDownload.Core.Transport;

/// <summary>
/// HTTP/HTTPS <see cref="ITransport"/> (TASK-023). Issues GET/HEAD requests, optionally with a
/// <c>Range</c> header, and returns the response once headers are read (the body streams lazily). A ranged
/// request also asks for <c>identity</c> encoding so byte offsets stay meaningful for segmented resume —
/// automatic decompression would otherwise make the received bytes not line up with the requested range.
/// The client is chosen per request from <see cref="IHttpClientProvider"/> by the effective connection
/// profile (TASK-034/035), so a download routes through its proxy and answers auth challenges with its
/// credentials.
/// </summary>
internal sealed class HttpTransport : ITransport
{
    private readonly IHttpClientProvider _clientProvider;
    private readonly IProxyService _proxy;
    private readonly ICredentialContext _credentials;

    public HttpTransport(IHttpClientProvider clientProvider, IProxyService proxy, ICredentialContext credentials)
    {
        ArgumentNullException.ThrowIfNull(clientProvider);
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(credentials);
        _clientProvider = clientProvider;
        _proxy = proxy;
        _credentials = credentials;
    }

    public async Task<ITransportResponse> SendAsync(
        TransportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var profile = new ConnectionProfile(_proxy.Effective, _credentials.Effective);
        HttpClient client = _clientProvider.GetClient(profile);

        using var message = new HttpRequestMessage(
            request.Method == TransportMethod.Head ? HttpMethod.Head : HttpMethod.Get,
            request.Uri);

        if (request.Range is { } range)
        {
            message.Headers.Range = new RangeHeaderValue(range.From, range.To);
            // Keep received bytes aligned with the requested byte range (no transparent gzip/brotli).
            message.Headers.AcceptEncoding.Clear();
            message.Headers.AcceptEncoding.ParseAdd("identity");
        }

        foreach (KeyValuePair<string, string> header in request.Headers)
        {
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // ResponseHeadersRead: return as soon as headers arrive; the body is streamed by the caller.
        HttpResponseMessage response = await client
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        return new HttpTransportResponse(response, request.Uri);
    }
}
