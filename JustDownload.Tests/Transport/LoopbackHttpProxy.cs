using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace JustDownload.Tests.Transport;

/// <summary>
/// A tiny forwarding HTTP proxy for proxy-routing tests (TASK-034). It accepts absolute-form proxied
/// requests (<c>GET http://origin/path HTTP/1.1</c>) that .NET's handler sends when a proxy is configured,
/// records each requested URL (so a test can prove traffic was routed through it), forwards the request —
/// relaying the <c>Range</c> header — to the origin, and relays the response (status, content-range,
/// content-length, body) back. Plain HTTP only (no <c>CONNECT</c> tunneling), which suffices for the
/// loopback origin. Torn down on <see cref="DisposeAsync"/>.
/// </summary>
internal sealed class LoopbackHttpProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly HttpClient _forwarder;
    private readonly object _gate = new();
    private readonly List<string> _requested = [];

    public LoopbackHttpProxy()
    {
        _forwarder = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>The proxy's listening port on loopback.</summary>
    public int Port { get; }

    /// <summary>When set to <c>user:pass</c>, the proxy demands Basic <c>Proxy-Authorization</c> (407) first.</summary>
    public string? RequiredBasicAuth { get; set; }

    /// <summary>The absolute URLs the proxy was asked to fetch (proves routing through the proxy).</summary>
    public IReadOnlyList<string> RequestedUrls
    {
        get
        {
            lock (_gate)
            {
                return _requested.ToArray();
            }
        }
    }

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

            _ = HandleAsync(client, ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                (string? target, string? range, string? proxyAuth) =
                    await ReadRequestAsync(stream, ct).ConfigureAwait(false);
                if (target is null)
                {
                    return;
                }

                if (RequiredBasicAuth is { } required && !IsProxyAuthValid(proxyAuth, required))
                {
                    await WriteProxyChallengeAsync(stream, ct).ConfigureAwait(false);
                    return;
                }

                lock (_gate)
                {
                    _requested.Add(target);
                }

                using var forward = new HttpRequestMessage(HttpMethod.Get, target);
                if (range is not null)
                {
                    forward.Headers.TryAddWithoutValidation("Range", range);
                }

                using HttpResponseMessage response = await _forwarder
                    .SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                byte[] body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

                await WriteResponseAsync(stream, response, body, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static bool IsProxyAuthValid(string? proxyAuth, string required)
    {
        const string prefix = "Basic ";
        if (proxyAuth is null || !proxyAuth.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(proxyAuth[prefix.Length..].Trim()));
        return decoded == required;
    }

    private static async Task WriteProxyChallengeAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 407 Proxy Authentication Required\r\n" +
            "Proxy-Authenticate: Basic realm=\"proxy\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<(string? Target, string? Range, string? ProxyAuth)> ReadRequestAsync(
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
        if (lines.Length == 0)
        {
            return (null, null, null);
        }

        string[] requestLine = lines[0].Split(' ');
        string? target = requestLine.Length >= 2 ? requestLine[1] : null;
        string? range = null;
        string? proxyAuth = null;
        foreach (string line in lines)
        {
            if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
            {
                range = line["Range:".Length..].Trim();
            }
            else if (line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
            {
                proxyAuth = line["Proxy-Authorization:".Length..].Trim();
            }
        }

        return (target, range, proxyAuth);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream, HttpResponseMessage response, byte[] body, CancellationToken ct)
    {
        int status = (int)response.StatusCode;
        string reason = status switch { 200 => "OK", 206 => "Partial Content", _ => "Status" };
        var headers = new StringBuilder();
        headers.Append(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n");
        if (response.Content.Headers.ContentRange is { } cr)
        {
            headers.Append(CultureInfo.InvariantCulture, $"Content-Range: {cr}\r\n");
        }

        headers.Append("Accept-Ranges: bytes\r\n");
        headers.Append("Connection: close\r\n");

        byte[] head = Encoding.ASCII.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {reason}\r\n{headers}\r\n"));
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        _forwarder.Dispose();
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
