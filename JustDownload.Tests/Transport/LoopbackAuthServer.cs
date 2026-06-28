using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace JustDownload.Tests.Transport;

/// <summary>The HTTP auth scheme a <see cref="LoopbackAuthServer"/> challenges with (TASK-035).</summary>
public enum AuthScheme
{
    Basic,
    Digest,
}

/// <summary>
/// A loopback HTTP server that requires authentication (TASK-035): it answers an unauthenticated request
/// with a <c>401</c> challenge (Basic or Digest, RFC 7616 MD5/qop=auth) and serves the body only once the
/// client returns a valid <c>Authorization</c> header — exercising .NET's real challenge-response with the
/// credentials carried on the handler. Range-aware so the resource probe's <c>bytes=0-0</c> works. NTLM is
/// out of scope for an in-process server and is validated against a real server by the fixture task.
/// </summary>
internal sealed class LoopbackAuthServer : IAsyncDisposable
{
    private const string Realm = "justdownload";
    private const string Nonce = "deadbeefcafe";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public LoopbackAuthServer(AuthScheme scheme, string username, string password, byte[] body)
    {
        Scheme = scheme;
        Username = username;
        Password = password;
        Body = body;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUri = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public AuthScheme Scheme { get; }

    public string Username { get; }

    public string Password { get; }

    public byte[] Body { get; }

    public Uri BaseUri { get; }

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
                (string method, string? authorization, (long From, long? To)? range) =
                    await ReadRequestAsync(stream, ct).ConfigureAwait(false);

                if (!IsAuthorized(method, authorization))
                {
                    await WriteChallengeAsync(stream, ct).ConfigureAwait(false);
                    return;
                }

                await WriteBodyAsync(stream, method, range, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private bool IsAuthorized(string method, string? authorization)
    {
        if (authorization is null)
        {
            return false;
        }

        return Scheme == AuthScheme.Basic
            ? IsBasicValid(authorization)
            : IsDigestValid(method, authorization);
    }

    private bool IsBasicValid(string authorization)
    {
        const string prefix = "Basic ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[prefix.Length..].Trim()));
        return decoded == $"{Username}:{Password}";
    }

    private bool IsDigestValid(string method, string authorization)
    {
        const string prefix = "Digest ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Dictionary<string, string> p = ParseDigest(authorization[prefix.Length..]);
        if (!p.TryGetValue("username", out string? user) || user != Username ||
            !p.TryGetValue("uri", out string? uri) ||
            !p.TryGetValue("response", out string? response) ||
            !p.TryGetValue("nc", out string? nc) ||
            !p.TryGetValue("cnonce", out string? cnonce))
        {
            return false;
        }

        string qop = p.GetValueOrDefault("qop", "auth");
        string ha1 = Md5($"{Username}:{Realm}:{Password}");
        string ha2 = Md5($"{method}:{uri}");
        string expected = Md5($"{ha1}:{Nonce}:{nc}:{cnonce}:{qop}:{ha2}");
        return string.Equals(expected, response, StringComparison.OrdinalIgnoreCase);
    }

    private async Task WriteChallengeAsync(NetworkStream stream, CancellationToken ct)
    {
        string challenge = Scheme == AuthScheme.Basic
            ? string.Create(CultureInfo.InvariantCulture, $"Basic realm=\"{Realm}\"")
            : string.Create(CultureInfo.InvariantCulture, $"Digest realm=\"{Realm}\", qop=\"auth\", nonce=\"{Nonce}\"");

        byte[] head = Encoding.ASCII.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: {challenge}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task WriteBodyAsync(
        NetworkStream stream, string method, (long From, long? To)? range, CancellationToken ct)
    {
        byte[] slice;
        int status;
        string reason;
        var headers = new StringBuilder();

        if (range is { } r)
        {
            long from = Math.Max(0, r.From);
            long to = Math.Min(r.To ?? (Body.Length - 1), Body.Length - 1);
            slice = Body[(int)from..(int)(to + 1)];
            status = 206;
            reason = "Partial Content";
            headers.Append(CultureInfo.InvariantCulture, $"Content-Range: bytes {from}-{to}/{Body.Length}\r\n");
        }
        else
        {
            slice = Body;
            status = 200;
            reason = "OK";
        }

        headers.Append("Accept-Ranges: bytes\r\n");
        headers.Append(CultureInfo.InvariantCulture, $"Content-Length: {slice.Length}\r\n");
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

    private static async Task<(string Method, string? Authorization, (long From, long? To)? Range)> ReadRequestAsync(
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
        string? authorization = null;
        (long, long?)? range = null;

        foreach (string line in lines)
        {
            if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
            {
                authorization = line["Authorization:".Length..].Trim();
            }
            else if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
            {
                string value = line["Range:".Length..].Trim();
                const string p = "bytes=";
                if (value.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = value[p.Length..].Split('-');
                    long from = long.Parse(parts[0], CultureInfo.InvariantCulture);
                    long? to = parts.Length > 1 && parts[1].Length > 0
                        ? long.Parse(parts[1], CultureInfo.InvariantCulture)
                        : null;
                    range = (from, to);
                }
            }
        }

        return (method, authorization, range);
    }

    private static Dictionary<string, string> ParseDigest(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in value.Split(','))
        {
            int eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                continue;
            }

            string key = part[..eq].Trim();
            string val = part[(eq + 1)..].Trim().Trim('"');
            result[key] = val;
        }

        return result;
    }

    // HTTP Digest authentication (RFC 7616) mandates MD5 for the H() function; this is interop with the
    // protocol in a test fixture, not a security control. Suppress the broken-algorithm analyzer here.
#pragma warning disable CA5351
    private static string Md5(string input) =>
        Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(input))).ToLowerInvariant();
#pragma warning restore CA5351

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
