using System.Net.Http;
using System.Net.Http.Headers;

namespace JustDownload.Core.Transport;

/// <summary>
/// HTTP/HTTPS <see cref="ITransport"/> over the single shared <see cref="SocketsHttpHandler"/>
/// (TASK-023). Issues GET/HEAD requests, optionally with a <c>Range</c> header, and returns the response
/// once headers are read (the body streams lazily). A ranged request also asks for <c>identity</c>
/// encoding so byte offsets stay meaningful for segmented resume — automatic decompression on the shared
/// handler would otherwise make the received bytes not line up with the requested range.
/// </summary>
internal sealed class HttpTransport : ITransport, IDisposable
{
    private readonly HttpClient _client;

    public HttpTransport(ISharedHttpHandlerProvider handlerProvider, TransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(handlerProvider);
        ArgumentNullException.ThrowIfNull(options);

        // Reuse the one shared handler; disposeHandler:false so this client never tears it down.
        _client = new HttpClient(handlerProvider.Handler, disposeHandler: false)
        {
            // No overall timeout: large transfers are bounded by the caller's CancellationToken, not a
            // clock. Connect/idle timeouts live on the handler.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
    }

    public async Task<ITransportResponse> SendAsync(
        TransportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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
        HttpResponseMessage response = await _client
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        return new HttpTransportResponse(response, request.Uri);
    }

    public void Dispose() => _client.Dispose();
}
