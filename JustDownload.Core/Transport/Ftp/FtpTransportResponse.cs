namespace JustDownload.Core.Transport.Ftp;

/// <summary>
/// An <see cref="ITransportResponse"/> for an FTP transfer (TASK-033). It adapts FTP semantics to the
/// engine's HTTP-shaped seam: a ranged request maps to <c>REST</c> and is reported as <c>206 Partial
/// Content</c> with a synthesized <see cref="ContentRange"/> (so the probe learns the size and that
/// resume is supported), while a plain request is <c>200 OK</c>. The body stream and the underlying
/// connection are released on <see cref="DisposeAsync"/>.
/// </summary>
internal sealed class FtpTransportResponse : ITransportResponse
{
    private readonly IFtpConnection _connection;
    private readonly Stream? _body;

    private FtpTransportResponse(
        IFtpConnection connection,
        Stream? body,
        int statusCode,
        Uri finalUri,
        long? contentLength,
        ContentRange? contentRange,
        bool acceptsRanges,
        string suggestedFileName)
    {
        _connection = connection;
        _body = body;
        StatusCode = statusCode;
        FinalUri = finalUri;
        ContentLength = contentLength;
        ContentRange = contentRange;
        AcceptsRanges = acceptsRanges;
        SuggestedFileName = suggestedFileName;
    }

    public int StatusCode { get; }

    public bool IsSuccessStatusCode => StatusCode is >= 200 and < 300;

    public bool IsPartialContent => StatusCode == 206;

    public Uri FinalUri { get; }

    public long? ContentLength { get; }

    public ContentRange? ContentRange { get; }

    public bool AcceptsRanges { get; }

    public string SuggestedFileName { get; }

    public string? ETag => null;

    public DateTimeOffset? LastModified => null;

    /// <summary>Builds a metadata-only response (a <c>HEAD</c>-equivalent); the connection is already closed.</summary>
    public static FtpTransportResponse Metadata(
        IFtpConnection connection, Uri uri, long size, string fileName) =>
        new(connection, body: null, statusCode: 200, uri,
            contentLength: size >= 0 ? size : null, contentRange: null,
            acceptsRanges: size >= 0, fileName);

    /// <summary>Builds a body response for a (possibly resumed) read.</summary>
    public static FtpTransportResponse Body(
        IFtpConnection connection, Stream stream, Uri uri, long size, long from, bool ranged, string fileName)
    {
        int status = ranged ? 206 : 200;
        ContentRange? range = ranged && size >= 0 ? new ContentRange(from, size - 1, size) : null;
        long? length = size >= 0 ? (ranged ? size - from : size) : null;
        return new FtpTransportResponse(connection, stream, status, uri, length, range, size >= 0, fileName);
    }

    public Task<Stream> OpenContentStreamAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_body ?? Stream.Null);

    public async ValueTask DisposeAsync()
    {
        if (_body is not null)
        {
            await _body.DisposeAsync().ConfigureAwait(false);
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
