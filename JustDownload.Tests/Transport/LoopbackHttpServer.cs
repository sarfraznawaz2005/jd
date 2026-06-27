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

    /// <summary>The <c>Content-Disposition</c> header value to send, or <see langword="null"/> for none.</summary>
    public string? ContentDisposition { get; set; }

    /// <summary>The <c>Content-Type</c> header value to send.</summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>
    /// Whether to send <c>Content-Length</c>. When <see langword="false"/> the body is delimited by the
    /// connection close (so the client sees an unknown length) — used to test the unknown-size path.
    /// </summary>
    public bool SendContentLength { get; set; } = true;

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

            _ = HandleConnectionAsync(client, ct);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
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
        }
    }

    private static async Task<(string Method, (long From, long? To)? Range)> ReadRequestAsync(
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
        byte[] body = Body;
        byte[] slice;
        int status;
        string reason;
        var headers = new StringBuilder();

        if (range is { } r && SupportRanges && body.Length > 0)
        {
            long from = Math.Max(0, r.From);
            long to = Math.Min(r.To ?? (body.Length - 1), body.Length - 1);
            slice = body[(int)from..(int)(to + 1)];
            status = 206;
            reason = "Partial Content";
            headers.Append(CultureInfo.InvariantCulture, $"Content-Range: bytes {from}-{to}/{body.Length}\r\n");
        }
        else
        {
            slice = body;
            status = 200;
            reason = "OK";
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
