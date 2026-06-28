using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Transport.Ftp;

/// <summary>
/// FTP/FTPS <see cref="ITransport"/> (TASK-033, US-5). Adapts FTP to the engine's transport seam so the
/// existing probe and segmented downloader work unchanged: a ranged request becomes a <c>REST</c>-resumed
/// read reported as <c>206</c> (enabling segmentation and resume from a persisted offset — AC1), a plain
/// request is a full read, and the file name is taken from the URL path or, when the path is a directory,
/// from a directory listing (AC2). FTPS is selected by the <c>ftps://</c> scheme. Connects in passive mode
/// (handled by the connection factory).
/// </summary>
internal sealed partial class FtpTransport : ITransport
{
    private readonly IFtpConnectionFactory _factory;
    private readonly ILogger<FtpTransport> _logger;

    public FtpTransport(IFtpConnectionFactory factory, ILogger<FtpTransport> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _logger = logger;
    }

    public async Task<ITransportResponse> SendAsync(
        TransportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string path = Uri.UnescapeDataString(request.Uri.AbsolutePath);
        IFtpConnection connection = _factory.Create(request.Uri);
        bool keepConnection = false;
        try
        {
            await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);

            long size = await connection.GetFileSizeAsync(path, cancellationToken).ConfigureAwait(false);
            string fileName = await ResolveFileNameAsync(connection, path, cancellationToken).ConfigureAwait(false);

            if (request.Method == TransportMethod.Head)
            {
                keepConnection = true; // ownership passes to the response, which disposes it
                return FtpTransportResponse.Metadata(connection, request.Uri, size, fileName);
            }

            long from = request.Range?.From ?? 0;
            bool ranged = request.Range is not null;
            Stream stream = await connection.OpenReadAsync(path, from, cancellationToken).ConfigureAwait(false);
            keepConnection = true; // ownership passes to the response, which disposes it
            LogOpened(_logger, request.Uri, from, size);
            return FtpTransportResponse.Body(connection, stream, request.Uri, size, from, ranged, fileName);
        }
        finally
        {
            if (!keepConnection)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> ResolveFileNameAsync(
        IFtpConnection connection, string path, CancellationToken cancellationToken)
    {
        // A path with a concrete last segment (not a directory) names the file directly.
        bool isDirectory = path.Length == 0 || path.EndsWith('/');
        if (!isDirectory)
        {
            string name = GetLastSegment(path);
            if (name.Length > 0)
            {
                return name;
            }
        }

        // The path is a directory (or has no usable name) — derive a name from the listing (AC2).
        string directory = path.Length == 0 ? "/" : path;
        IReadOnlyList<string> names = await connection.ListNamesAsync(directory, cancellationToken)
            .ConfigureAwait(false);
        string? first = names.Select(GetLastSegment).FirstOrDefault(n => n.Length > 0);
        return first ?? "download";
    }

    private static string GetLastSegment(string path)
    {
        string trimmed = path.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "FTP read {Uri} from offset {From} (size {Size}).")]
    private static partial void LogOpened(ILogger logger, Uri uri, long from, long size);
}
