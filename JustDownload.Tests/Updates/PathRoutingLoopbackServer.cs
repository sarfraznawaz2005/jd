using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace JustDownload.Tests.Updates;

/// <summary>
/// A tiny in-process HTTP/1.1 server that routes by exact request path (TASK-080). Unlike
/// <see cref="JustDownload.Tests.Transport.LoopbackHttpServer"/> — purpose-built for range/resume file
/// serving — the update checker needs several distinct endpoints on one server (the release JSON,
/// <c>checksums.txt</c>, <c>checksums.txt.sig</c>, and a fake installer asset), so this fixture routes by
/// path instead of serving one configurable body. Built on <see cref="TcpListener"/> for the same reasons
/// as its sibling: no URL-ACL/admin rights, and it's torn down on <see cref="DisposeAsync"/> so no listener
/// leaks between tests.
/// </summary>
internal sealed class PathRoutingLoopbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Dictionary<string, RouteResponse> _routes = new(StringComparer.Ordinal);
    private int _requestCount;

    public PathRoutingLoopbackServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUri = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>The server's base URL (loopback, ephemeral port).</summary>
    public Uri BaseUri { get; }

    /// <summary>Total requests handled so far — used to assert zero calls when a feature is disabled (AC2).</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>Builds an absolute URL for <paramref name="relativePath"/> under this server.</summary>
    public Uri Url(string relativePath) => new(BaseUri, relativePath);

    /// <summary>Registers a raw-bytes response for an exact request path (e.g. <c>"/checksums.txt"</c>).</summary>
    public void Route(string path, byte[] body, string contentType = "application/octet-stream") =>
        _routes[NormalizePath(path)] = new RouteResponse(body, contentType);

    /// <summary>Registers a UTF-8 text response.</summary>
    public void RouteText(string path, string text, string contentType = "text/plain") =>
        Route(path, Encoding.UTF8.GetBytes(text), contentType);

    /// <summary>Registers a UTF-8 JSON response.</summary>
    public void RouteJson(string path, string json) => Route(path, Encoding.UTF8.GetBytes(json), "application/json");

    private static string NormalizePath(string path) => path.StartsWith('/') ? path : "/" + path;

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
                string path = await ReadRequestPathAsync(stream, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _requestCount);
                await WriteResponseAsync(stream, path, ct).ConfigureAwait(false);
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

    private static async Task<string> ReadRequestPathAsync(NetworkStream stream, CancellationToken ct)
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

        string requestLine = text.ToString().Split("\r\n")[0];
        string[] parts = requestLine.Split(' ');
        string rawPath = parts.Length > 1 ? parts[1] : "/";
        int query = rawPath.IndexOf('?', StringComparison.Ordinal);
        return query >= 0 ? rawPath[..query] : rawPath;
    }

    private async Task WriteResponseAsync(NetworkStream stream, string path, CancellationToken ct)
    {
        if (!_routes.TryGetValue(path, out RouteResponse? response))
        {
            byte[] notFoundHead = Encoding.ASCII.GetBytes(
                "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(notFoundHead, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            return;
        }

        var headers = new StringBuilder();
        headers.Append(CultureInfo.InvariantCulture, $"Content-Type: {response.ContentType}\r\n");
        headers.Append(CultureInfo.InvariantCulture, $"Content-Length: {response.Body.Length}\r\n");
        headers.Append("Connection: close\r\n");

        byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\n{headers}\r\n");
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.WriteAsync(response.Body, ct).ConfigureAwait(false);
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

    private sealed record RouteResponse(byte[] Body, string ContentType);
}
