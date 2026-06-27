using System.Net;
using System.Net.Http;

namespace JustDownload.Core.Transport;

/// <summary>
/// <see cref="ITransportResponse"/> over an <see cref="HttpResponseMessage"/> (TASK-023). Reads the
/// headers the engine needs eagerly (so the message can be inspected without buffering the body) and
/// streams the body on demand. Disposing releases the underlying connection back to the shared pool.
/// </summary>
internal sealed class HttpTransportResponse : ITransportResponse
{
    private readonly HttpResponseMessage _response;

    public HttpTransportResponse(HttpResponseMessage response, Uri requestUri)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(requestUri);
        _response = response;

        StatusCode = (int)response.StatusCode;
        IsSuccessStatusCode = response.IsSuccessStatusCode;
        IsPartialContent = response.StatusCode == HttpStatusCode.PartialContent;
        FinalUri = response.RequestMessage?.RequestUri ?? requestUri;
        ContentLength = response.Content.Headers.ContentLength;

        if (response.Content.Headers.ContentRange is { } range)
        {
            ContentRange = new ContentRange(range.From, range.To, range.Length);
        }

        AcceptsRanges = IsPartialContent || response.Headers.AcceptRanges.Contains("bytes");

        string? contentDisposition =
            response.Content.Headers.TryGetValues("Content-Disposition", out IEnumerable<string>? cd)
                ? cd.FirstOrDefault()
                : null;
        SuggestedFileName = HttpFileNameResolver.Resolve(contentDisposition, FinalUri);

        ETag = response.Headers.ETag?.ToString();
        LastModified = response.Content.Headers.LastModified;
    }

    public int StatusCode { get; }

    public bool IsSuccessStatusCode { get; }

    public bool IsPartialContent { get; }

    public Uri FinalUri { get; }

    public long? ContentLength { get; }

    public ContentRange? ContentRange { get; }

    public bool AcceptsRanges { get; }

    public string SuggestedFileName { get; }

    public string? ETag { get; }

    public DateTimeOffset? LastModified { get; }

    public Task<Stream> OpenContentStreamAsync(CancellationToken cancellationToken = default) =>
        _response.Content.ReadAsStreamAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        _response.Dispose();
        return ValueTask.CompletedTask;
    }
}
