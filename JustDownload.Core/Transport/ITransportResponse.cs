namespace JustDownload.Core.Transport;

/// <summary>
/// The response to a <see cref="TransportRequest"/> (TASK-023): status and the metadata the download
/// engine needs — final URL after redirects, content length, the served <see cref="ContentRange"/>,
/// whether the server accepts ranges, the suggested file name, and validators (<see cref="ETag"/> /
/// <see cref="LastModified"/>) used for resume and expiry checks. The body is opened on demand via
/// <see cref="OpenContentStreamAsync"/> so headers can be inspected without buffering the payload.
/// Dispose to release the underlying connection.
/// </summary>
public interface ITransportResponse : IAsyncDisposable
{
    /// <summary>The numeric HTTP status code (e.g. 200, 206, 404).</summary>
    int StatusCode { get; }

    /// <summary>Whether <see cref="StatusCode"/> is in the 2xx success range.</summary>
    bool IsSuccessStatusCode { get; }

    /// <summary>Whether the response is <c>206 Partial Content</c> (a ranged request was honoured).</summary>
    bool IsPartialContent { get; }

    /// <summary>The resource URL after any redirects were followed.</summary>
    Uri FinalUri { get; }

    /// <summary>The body length in bytes when the server reports it; otherwise <see langword="null"/>.</summary>
    long? ContentLength { get; }

    /// <summary>The parsed <c>Content-Range</c> for a partial response; otherwise <see langword="null"/>.</summary>
    ContentRange? ContentRange { get; }

    /// <summary>Whether the server advertises byte-range support (<c>Accept-Ranges: bytes</c> or a 206).</summary>
    bool AcceptsRanges { get; }

    /// <summary>
    /// The file name derived from <c>Content-Disposition</c>, falling back to the URL (TASK-023 AC1).
    /// Never <see langword="null"/> — a safe default is used when nothing usable is present.
    /// </summary>
    string SuggestedFileName { get; }

    /// <summary>The strong/weak <c>ETag</c> validator, or <see langword="null"/> if absent.</summary>
    string? ETag { get; }

    /// <summary>The <c>Last-Modified</c> validator, or <see langword="null"/> if absent.</summary>
    DateTimeOffset? LastModified { get; }

    /// <summary>Opens the response body as a readable stream (headers are already available).</summary>
    Task<Stream> OpenContentStreamAsync(CancellationToken cancellationToken = default);
}
