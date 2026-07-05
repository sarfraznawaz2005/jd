using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace JustDownload.Tests.Transport;

/// <summary>
/// A tiny in-process HTTP/1.1 server for transport integration tests (TASK-023). Built on
/// <see cref="TcpListener"/> (not <see cref="HttpListener"/>) so it needs no URL-ACL/admin rights and
/// gives full control over <c>Range</c> handling, <c>Content-Disposition</c>, and <c>Accept-Ranges</c>.
/// It serves a single configurable byte body, honours a single <c>bytes=from-to</c> range, closes each
/// connection, and is torn down on <see cref="DisposeAsync"/> so no listener leaks between tests.
/// (TASK-082 will grow the full fixture matrix; this is the minimum TASK-023 needs.)
/// </summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly object _servedGate = new();
    private readonly List<(long From, long To)> _served = [];
    private readonly List<string> _receivedHeaderLines = [];
    private int _currentConnections;
    private int _maxConnections;

    public LoopbackHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUri = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>The body served for every request.</summary>
    public byte[] Body { get; set; } = [];

    /// <summary>Whether the server honours <c>Range</c> and advertises <c>Accept-Ranges: bytes</c>.</summary>
    public bool SupportRanges { get; set; } = true;

    /// <summary>
    /// When set, the server still answers the 1-byte range probe with <c>206</c> (so a resume is attempted)
    /// but ignores any multi-byte range, returning the full <c>200</c> body — simulating a server that can no
    /// longer resume from an offset (US-2 AC3).
    /// </summary>
    public bool IgnoreMultiByteRanges { get; set; }

    /// <summary>The <c>Content-Disposition</c> header value to send, or <see langword="null"/> for none.</summary>
    public string? ContentDisposition { get; set; }

    /// <summary>The <c>Content-Type</c> header value to send.</summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>
    /// Whether to send <c>Content-Length</c>. When <see langword="false"/> the body is delimited by the
    /// connection close (so the client sees an unknown length) — used to test the unknown-size path.
    /// </summary>
    public bool SendContentLength { get; set; } = true;

    /// <summary>
    /// When set, every response is answered with this status code and an empty body — used to simulate an
    /// expired/withdrawn link (e.g. <c>403</c>/<c>410</c>).
    /// </summary>
    public int? StatusOverride { get; set; }

    /// <summary>A delay applied to every response before it is written (holds the connection open).</summary>
    public TimeSpan ResponseDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Requests whose range starts at or beyond this offset get an extra <see cref="SlowTailDelay"/>.</summary>
    public long SlowTailFrom { get; set; } = long.MaxValue;

    /// <summary>Extra delay for "slow tail" requests (used to provoke work-stealing deterministically).</summary>
    public TimeSpan SlowTailDelay { get; set; } = TimeSpan.Zero;

    /// <summary>The peak number of simultaneously-open connections the server has seen.</summary>
    public int MaxConcurrentConnections => Volatile.Read(ref _maxConnections);

    /// <summary>The inclusive byte ranges actually sent as response bodies, in order served.</summary>
    public IReadOnlyList<(long From, long To)> ServedRanges
    {
        get
        {
            lock (_servedGate)
            {
                return _served.ToArray();
            }
        }
    }

    /// <summary>The total number of body bytes served so far (across all requests).</summary>
    public long ServedBytes
    {
        get
        {
            lock (_servedGate)
            {
                long total = 0;
                foreach ((long from, long to) in _served)
                {
                    total += to - from + 1;
                }

                return total;
            }
        }
    }

    /// <summary>Resets the served-range log (used to isolate a resume session from the prior one).</summary>
    public void ClearServedRanges()
    {
        lock (_servedGate)
        {
            _served.Clear();
        }
    }

    /// <summary>Every request header line (<c>Name: value</c>) seen across all requests, in arrival order.</summary>
    public IReadOnlyList<string> ReceivedHeaderLines
    {
        get
        {
            lock (_servedGate)
            {
                return _receivedHeaderLines.ToArray();
            }
        }
    }

    /// <summary>The server's base URL (loopback, ephemeral port).</summary>
    public Uri BaseUri { get; }

    /// <summary>Builds an absolute URL for <paramref name="relativePath"/> under this server.</summary>
    public Uri Url(string relativePath) => new(BaseUri, relativePath);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                // On Linux, cancelling a pending AcceptTcpClientAsync sometimes surfaces as a raw
                // SocketException ("Operation canceled") instead of OperationCanceledException — the same
                // shutdown signal as the other two catches, just spelled differently on this platform.
                break;
            }

            _ = HandleConnectionAsync(client, ct);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            UpdateMax(Interlocked.Increment(ref _currentConnections));
            NetworkStream stream = client.GetStream();
            try
            {
                (string method, (long From, long? To)? range) = await ReadRequestAsync(stream, ct)
                    .ConfigureAwait(false);
                await WriteResponseAsync(stream, method, range, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Connection torn down mid-exchange (e.g. test finished) — ignore.
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                Interlocked.Decrement(ref _currentConnections);
            }
        }
    }

    private void UpdateMax(int candidate)
    {
        int max;
        do
        {
            max = Volatile.Read(ref _maxConnections);
            if (candidate <= max)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _maxConnections, candidate, max) != max);
    }

    private async Task<(string Method, (long From, long? To)? Range)> ReadRequestAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var text = new StringBuilder();
        while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal) && text.Length < 65536)
        {
            int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        string[] lines = text.ToString().Split("\r\n");
        string method = lines.Length > 0 ? lines[0].Split(' ').FirstOrDefault() ?? "GET" : "GET";

        // Record header lines (everything after the request line, up to the blank line) for assertions.
        lock (_servedGate)
        {
            foreach (string line in lines.Skip(1))
            {
                if (line.Length == 0)
                {
                    break;
                }

                _receivedHeaderLines.Add(line);
            }
        }

        (long, long?)? range = null;
        foreach (string line in lines)
        {
            if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = line["Range:".Length..].Trim();
            const string prefix = "bytes=";
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parts = value[prefix.Length..].Split('-');
            long from = long.Parse(parts[0], CultureInfo.InvariantCulture);
            long? to = parts.Length > 1 && parts[1].Length > 0
                ? long.Parse(parts[1], CultureInfo.InvariantCulture)
                : null;
            range = (from, to);
        }

        return (method, range);
    }

    private async Task WriteResponseAsync(
        NetworkStream stream, string method, (long From, long? To)? range, CancellationToken ct)
    {
        TimeSpan delay = ResponseDelay;
        if (range is { } requested && requested.From >= SlowTailFrom)
        {
            delay += SlowTailDelay;
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        if (StatusOverride is { } overrideStatus)
        {
            byte[] errorHead = Encoding.ASCII.GetBytes(string.Create(
                CultureInfo.InvariantCulture,
                $"HTTP/1.1 {overrideStatus} Status\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(errorHead, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            return;
        }

        byte[] body = Body;
        byte[] slice;
        int status;
        string reason;
        var headers = new StringBuilder();

        long servedFrom;
        long servedTo;
        bool isSingleByteProbe = range is { } probe && probe.To == probe.From;
        bool honourRange = range is not null && SupportRanges && body.Length > 0 &&
            (!IgnoreMultiByteRanges || isSingleByteProbe);

        if (honourRange && range is { } r)
        {
            long from = Math.Max(0, r.From);
            long to = Math.Min(r.To ?? (body.Length - 1), body.Length - 1);
            slice = body[(int)from..(int)(to + 1)];
            status = 206;
            reason = "Partial Content";
            headers.Append(CultureInfo.InvariantCulture, $"Content-Range: bytes {from}-{to}/{body.Length}\r\n");
            servedFrom = from;
            servedTo = to;
        }
        else
        {
            slice = body;
            status = 200;
            reason = "OK";
            servedFrom = 0;
            servedTo = body.Length - 1;
        }

        if (!string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase) && slice.Length > 0)
        {
            lock (_servedGate)
            {
                _served.Add((servedFrom, servedTo));
            }
        }

        headers.Append(CultureInfo.InvariantCulture, $"Content-Type: {ContentType}\r\n");
        if (SendContentLength)
        {
            headers.Append(CultureInfo.InvariantCulture, $"Content-Length: {slice.Length}\r\n");
        }

        headers.Append(SupportRanges ? "Accept-Ranges: bytes\r\n" : "Accept-Ranges: none\r\n");
        if (ContentDisposition is not null)
        {
            headers.Append(CultureInfo.InvariantCulture, $"Content-Disposition: {ContentDisposition}\r\n");
        }

        headers.Append("Connection: close\r\n");

        byte[] head = Encoding.ASCII.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {reason}\r\n{headers}\r\n"));
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        if (!string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase) && slice.Length > 0)
        {
            await stream.WriteAsync(slice, ct).ConfigureAwait(false);
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }
}
