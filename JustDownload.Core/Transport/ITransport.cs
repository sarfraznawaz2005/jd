namespace JustDownload.Core.Transport;

/// <summary>
/// The engine's transport seam (TASK-023): issues a <see cref="TransportRequest"/> and returns the
/// response headers plus an on-demand body stream. The HTTP/HTTPS implementation is
/// <c>HttpTransport</c> over a single shared pooled handler; sibling transports (FTP — TASK-033) and
/// cross-cutting concerns layered onto HTTP (proxy — TASK-034, authentication — TASK-035) build on this
/// abstraction. The download engine depends only on this interface, never on a concrete client.
/// </summary>
public interface ITransport
{
    /// <summary>
    /// Sends <paramref name="request"/> and returns the response once the headers are read (the body is
    /// streamed lazily). The caller disposes the returned <see cref="ITransportResponse"/>.
    /// </summary>
    /// <param name="request">The request to send.</param>
    /// <param name="cancellationToken">Cancels the send and the in-flight transfer.</param>
    Task<ITransportResponse> SendAsync(
        TransportRequest request,
        CancellationToken cancellationToken = default);
}
