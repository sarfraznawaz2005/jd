using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace JustDownload.Tests.Transport;

/// <summary>
/// A tiny forwarding HTTP proxy for proxy-routing tests (TASK-034). It accepts absolute-form proxied
/// requests (<c>GET http://origin/path HTTP/1.1</c>) that .NET's handler sends when a proxy is configured,
/// records each requested URL (so a test can prove traffic was routed through it), forwards the request —
/// relaying the <c>Range</c> header — to the origin, and relays the response (status, content-range,
/// content-length, body) back. Plain HTTP only (no <c>CONNECT</c> tunneling), which suffices for the
/// loopback origin. Basic/Digest challenges are answered per-request; when <see cref="RequiredNtlmAuth"/>
/// is set, the connection instead drives a real NTLMSSP handshake (TASK-111) over the persistent TCP
/// connection — same mechanics as <see cref="LoopbackNtlmAuthServer"/>, but on <c>Proxy-Authenticate</c>/
/// <c>Proxy-Authorization</c> headers and <c>407</c> instead of <c>WWW-Authenticate</c>/<c>Authorization</c>/
/// <c>401</c>. Torn down on <see cref="DisposeAsync"/>.
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

    private const string DigestRealm = "proxy";
    private const string DigestNonce = "cafef00dproxy";

    /// <summary>When set to <c>user:pass</c>, the proxy demands Basic <c>Proxy-Authorization</c> (407) first.</summary>
    public string? RequiredBasicAuth { get; set; }

    /// <summary>When set to <c>user:pass</c>, the proxy demands Digest <c>Proxy-Authorization</c> (407, RFC 7616).</summary>
    public string? RequiredDigestAuth { get; set; }

    /// <summary>
    /// When set to <c>user:pass</c>, the proxy demands a real NTLMv2 <c>Proxy-Authorization</c> handshake
    /// (407) over the persistent connection (TASK-111) — mutually exclusive with the Basic/Digest properties.
    /// </summary>
    public string? RequiredNtlmAuth { get; set; }

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
        if (RequiredNtlmAuth is not null)
        {
            await HandleNtlmConnectionAsync(client, ct).ConfigureAwait(false);
            return;
        }

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

                if (RequiredBasicAuth is { } basic && !IsBasicValid(proxyAuth, basic))
                {
                    await WriteProxyChallengeAsync(stream, "Basic realm=\"proxy\"", keepAlive: false, ct)
                        .ConfigureAwait(false);
                    return;
                }

                if (RequiredDigestAuth is { } digest && !IsDigestValid(proxyAuth, digest))
                {
                    await WriteProxyChallengeAsync(
                        stream, $"Digest realm=\"{DigestRealm}\", qop=\"auth\", nonce=\"{DigestNonce}\"",
                        keepAlive: false, ct).ConfigureAwait(false);
                    return;
                }

                await ForwardAsync(stream, target, range, keepAlive: false, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Drives a real NTLMSSP handshake (TASK-111) over one persistent proxy connection: an unauthenticated
    /// request gets a bare <c>NTLM</c> 407 challenge, the client's TYPE_1 gets a TYPE_2 challenge carrying a
    /// fresh server nonce, and the client's TYPE_3 is cryptographically verified (NTLMv2, same machinery as
    /// <see cref="LoopbackNtlmAuthServer"/>) before the connection is treated as authenticated and every
    /// further request on it is forwarded to the origin.
    /// </summary>
    private async Task HandleNtlmConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                string[] required = RequiredNtlmAuth!.Split(':', 2);
                string requiredUser = required[0];
                string requiredPassword = required.Length > 1 ? required[1] : string.Empty;

                byte[] serverChallenge = [];
                bool authenticated = false;

                while (true)
                {
                    (string? target, string? range, string? proxyAuth) =
                        await ReadRequestAsync(stream, ct).ConfigureAwait(false);
                    if (target is null)
                    {
                        return;
                    }

                    if (authenticated)
                    {
                        await ForwardAsync(stream, target, range, keepAlive: true, ct).ConfigureAwait(false);
                        continue;
                    }

                    if (proxyAuth is null || !proxyAuth.StartsWith("NTLM", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteProxyChallengeAsync(stream, "NTLM", keepAlive: true, ct).ConfigureAwait(false);
                        continue;
                    }

                    byte[] message = Convert.FromBase64String(proxyAuth["NTLM".Length..].Trim());
                    int messageType = message.Length >= 12 ? BitConverter.ToInt32(message, 8) : 0;

                    if (messageType == 1)
                    {
                        serverChallenge = RandomNumberGenerator.GetBytes(8);
                        byte[] challenge = NtlmMessages.BuildChallenge(serverChallenge, "PROXY-CORP", "PROXYSRV");
                        await WriteProxyChallengeAsync(
                            stream, $"NTLM {Convert.ToBase64String(challenge)}", keepAlive: true, ct)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (messageType == 3)
                    {
                        (string domain, string username, byte[] ntResponse) = NtlmMessages.ParseAuthenticate(message);
                        bool cryptoOk = NtlmMessages.VerifyNtlmV2(
                            domain, username, requiredPassword, serverChallenge, ntResponse);
                        authenticated = username.Equals(requiredUser, StringComparison.OrdinalIgnoreCase) && cryptoOk;

                        if (!authenticated)
                        {
                            await WriteProxyChallengeAsync(stream, "NTLM", keepAlive: true, ct).ConfigureAwait(false);
                            continue;
                        }

                        await ForwardAsync(stream, target, range, keepAlive: true, ct).ConfigureAwait(false);
                        continue;
                    }

                    await WriteProxyChallengeAsync(stream, "NTLM", keepAlive: true, ct).ConfigureAwait(false);
                }
            }
            catch (IOException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task ForwardAsync(
        NetworkStream stream, string target, string? range, bool keepAlive, CancellationToken ct)
    {
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

        await WriteResponseAsync(stream, response, body, keepAlive, ct).ConfigureAwait(false);
    }

    private static bool IsBasicValid(string? proxyAuth, string required)
    {
        const string prefix = "Basic ";
        if (proxyAuth is null || !proxyAuth.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(proxyAuth[prefix.Length..].Trim()));
        return decoded == required;
    }

    private static bool IsDigestValid(string? proxyAuth, string required)
    {
        const string prefix = "Digest ";
        if (proxyAuth is null || !proxyAuth.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] credential = required.Split(':', 2);
        string user = credential[0];
        string password = credential.Length > 1 ? credential[1] : string.Empty;

        Dictionary<string, string> p = ParseDigest(proxyAuth[prefix.Length..]);
        if (p.GetValueOrDefault("username") != user ||
            !p.TryGetValue("uri", out string? uri) ||
            !p.TryGetValue("response", out string? response) ||
            !p.TryGetValue("nc", out string? nc) ||
            !p.TryGetValue("cnonce", out string? cnonce))
        {
            return false;
        }

        string qop = p.GetValueOrDefault("qop", "auth");
        string ha1 = Md5($"{user}:{DigestRealm}:{password}");
        string ha2 = Md5($"GET:{uri}");
        string expected = Md5($"{ha1}:{DigestNonce}:{nc}:{cnonce}:{qop}:{ha2}");
        return string.Equals(expected, response, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ParseDigest(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in value.Split(','))
        {
            int eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                result[part[..eq].Trim()] = part[(eq + 1)..].Trim().Trim('"');
            }
        }

        return result;
    }

    // HTTP Digest (RFC 7616) mandates MD5 for H(); this is protocol interop in a test fixture, not security.
#pragma warning disable CA5351
    private static string Md5(string input) =>
        Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.ASCII.GetBytes(input))).ToLowerInvariant();
#pragma warning restore CA5351

    private static async Task WriteProxyChallengeAsync(
        NetworkStream stream, string challenge, bool keepAlive, CancellationToken ct)
    {
        string connection = keepAlive ? "keep-alive" : "close";
        byte[] head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 407 Proxy Authentication Required\r\n" +
            $"Proxy-Authenticate: {challenge}\r\nContent-Length: 0\r\nConnection: {connection}\r\n\r\n");
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
        NetworkStream stream, HttpResponseMessage response, byte[] body, bool keepAlive, CancellationToken ct)
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
        headers.Append(CultureInfo.InvariantCulture, $"Connection: {(keepAlive ? "keep-alive" : "close")}\r\n");

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
