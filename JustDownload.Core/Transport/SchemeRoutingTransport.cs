using JustDownload.Core.Transport.Ftp;

namespace JustDownload.Core.Transport;

/// <summary>
/// The composite <see cref="ITransport"/> the engine resolves (TASK-033). It dispatches each request to the
/// transport for its URL scheme — HTTP/HTTPS to <see cref="HttpTransport"/>, FTP/FTPS to
/// <see cref="FtpTransport"/> — so the probe and segmented downloader stay protocol-agnostic and depend
/// only on <see cref="ITransport"/>. New protocols are added by registering another scheme handler here.
/// </summary>
internal sealed class SchemeRoutingTransport : ITransport
{
    private readonly HttpTransport _http;
    private readonly FtpTransport _ftp;

    public SchemeRoutingTransport(HttpTransport http, FtpTransport ftp)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(ftp);
        _http = http;
        _ftp = ftp;
    }

    public Task<ITransportResponse> SendAsync(
        TransportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Uri.Scheme.ToLowerInvariant() switch
        {
            "http" or "https" => _http.SendAsync(request, cancellationToken),
            "ftp" or "ftps" => _ftp.SendAsync(request, cancellationToken),
            _ => throw new NotSupportedException($"No transport is registered for scheme '{request.Uri.Scheme}'."),
        };
    }
}
