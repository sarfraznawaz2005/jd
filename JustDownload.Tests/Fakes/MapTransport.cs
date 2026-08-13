using System.Collections.Concurrent;
using JustDownload.Core.Transport;

namespace JustDownload.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="ITransport"/> that serves configured byte bodies keyed by absolute URL — used by
/// media tests (HLS/DASH) to exercise playlist + segment + key fetching without sockets. It records every
/// requested URL and the peak concurrency, so a test can assert segments were fetched in parallel.
/// </summary>
internal sealed class MapTransport : ITransport
{
    private readonly ConcurrentDictionary<string, byte[]> _bodies = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<string> _requested = [];
    private readonly ConcurrentBag<ByteRange> _requestedRanges = [];
    private int _current;
    private int _peak;

    /// <summary>An optional delay applied to every response, to widen the window for concurrency.</summary>
    public TimeSpan ResponseDelay { get; set; } = TimeSpan.Zero;

    /// <summary>The absolute URLs requested so far (in no guaranteed order).</summary>
    public IReadOnlyCollection<string> RequestedUrls => _requested;

    /// <summary>The peak number of concurrently in-flight requests observed.</summary>
    public int PeakConcurrency => Volatile.Read(ref _peak);

    /// <summary>The byte ranges requested so far (in no guaranteed order).</summary>
    public IReadOnlyCollection<ByteRange> RequestedRanges => _requestedRanges;

    /// <summary>
    /// When set, a ranged request is answered with <c>200 OK</c> and the whole body — the adversarial
    /// "server ignores <c>Range</c>" behaviour a byte-range HLS download must not be corrupted by.
    /// </summary>
    public bool IgnoreRangeRequests { get; set; }

    /// <summary>Maps <paramref name="url"/> to a UTF-8 text body (e.g. a playlist).</summary>
    public MapTransport AddText(string url, string body)
    {
        _bodies[url] = System.Text.Encoding.UTF8.GetBytes(body);
        return this;
    }

    /// <summary>Maps <paramref name="url"/> to a raw byte body (e.g. a segment or key).</summary>
    public MapTransport AddBytes(string url, byte[] body)
    {
        _bodies[url] = body;
        return this;
    }

    public async Task<ITransportResponse> SendAsync(
        TransportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _requested.Add(request.Uri.ToString());

        int now = Interlocked.Increment(ref _current);
        UpdatePeak(now);
        try
        {
            if (ResponseDelay > TimeSpan.Zero)
            {
                await Task.Delay(ResponseDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _current);
        }

        if (!_bodies.TryGetValue(request.Uri.ToString(), out byte[]? body))
        {
            return new MapResponse(404, request.Uri, []);
        }

        if (request.Range is not { } range)
        {
            return new MapResponse(200, request.Uri, body);
        }

        _requestedRanges.Add(range);
        if (IgnoreRangeRequests)
        {
            return new MapResponse(200, request.Uri, body);
        }

        long last = Math.Min(range.To ?? body.Length - 1, body.Length - 1);
        if (range.From >= body.Length || last < range.From)
        {
            return new MapResponse(416, request.Uri, []);
        }

        byte[] slice = body[(int)range.From..(int)(last + 1)];
        return new MapResponse(
            206, request.Uri, slice, new ContentRange(range.From, last, body.Length));
    }

    private void UpdatePeak(int candidate)
    {
        int peak;
        do
        {
            peak = Volatile.Read(ref _peak);
            if (candidate <= peak)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _peak, candidate, peak) != peak);
    }

    private sealed class MapResponse : ITransportResponse
    {
        private readonly byte[] _body;

        public MapResponse(int statusCode, Uri uri, byte[] body, ContentRange? contentRange = null)
        {
            StatusCode = statusCode;
            FinalUri = uri;
            _body = body;
            ContentRange = contentRange;
        }

        public int StatusCode { get; }

        public bool IsSuccessStatusCode => StatusCode is >= 200 and < 300;

        public bool IsPartialContent => StatusCode == 206;

        public Uri FinalUri { get; }

        public long? ContentLength => _body.Length;

        public ContentRange? ContentRange { get; }

        public bool AcceptsRanges => IsPartialContent;

        public string SuggestedFileName => Path.GetFileName(FinalUri.AbsolutePath);

        public string? ETag => null;

        public DateTimeOffset? LastModified => null;

        public Task<Stream> OpenContentStreamAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(_body, writable: false));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
