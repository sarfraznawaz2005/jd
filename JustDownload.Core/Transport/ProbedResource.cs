namespace JustDownload.Core.Transport;

/// <summary>
/// What <see cref="IResourceProbe.OpenAsync"/> found: the <see cref="Result"/> metadata plus, when the
/// probe response already carries the complete resource, that still-open response as <see cref="Body"/>
/// so the caller can stream it instead of asking the server again (TASK-262). Re-asking costs a whole
/// extra round trip and outright fails on one-shot URLs — single-use tokens, signed links and CGI
/// endpoints that only answer once — where the second request is rejected and the error page lands on
/// disk as the "downloaded file". <see cref="Body"/> is <see langword="null"/> whenever the probe
/// response is not the whole resource (a one-byte <c>206</c>), leaving the caller to fetch normally.
/// Disposing releases the held response; disposing twice is safe, so a caller that decides not to use
/// the body can release its connection early.
/// </summary>
public sealed class ProbedResource : IAsyncDisposable
{
    private ITransportResponse? _body;

    internal ProbedResource(ResourceProbeResult result, ITransportResponse? body)
    {
        Result = result;
        _body = body;
    }

    /// <summary>The probe metadata: final URL, range support, size, file name and validators.</summary>
    public ResourceProbeResult Result { get; }

    /// <summary>
    /// The still-open response whose body is the complete resource from byte 0, or <see langword="null"/>
    /// when the probe kept none. Owned by this instance — do not dispose it directly.
    /// </summary>
    public ITransportResponse? Body => _body;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _body, null) is { } body)
        {
            await body.DisposeAsync().ConfigureAwait(false);
        }
    }
}
