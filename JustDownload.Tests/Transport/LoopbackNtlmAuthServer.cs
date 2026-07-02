using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace JustDownload.Tests.Transport;

/// <summary>
/// A loopback HTTP server that requires real NTLMv2 authentication (TASK-110): it drives the full
/// three-message NTLMSSP handshake (Negotiate/Challenge/Authenticate) over a persistent TCP connection —
/// NTLM is connection-oriented, not per-request, unlike Basic/Digest — and cryptographically verifies the
/// client's NTLMv2 response (HMAC-MD5 keyed on the actual password's NT hash) against the server challenge,
/// so a wrong password genuinely fails instead of a stubbed flag. Exercises .NET's real SSPI/NTLM
/// negotiation (<see cref="AuthTests.NtlmCredentials_WithDomain_RequireDedicatedHandler"/> only asserts the
/// plumbing; this validates the protocol end-to-end).
/// </summary>
internal sealed class LoopbackNtlmAuthServer : IAsyncDisposable
{
    private const string TargetDomain = "CORP";
    private const string TargetComputer = "SRV1";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public LoopbackNtlmAuthServer(string username, string password, string domain, byte[] body)
    {
        Username = username;
        Password = password;
        Domain = domain;
        Body = body;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUri = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public string Username { get; }

    public string Password { get; }

    public string Domain { get; }

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

            _ = HandleConnectionAsync(client, ct);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                byte[] serverChallenge = [];
                bool authenticated = false;

                while (true)
                {
                    Request? request = await ReadRequestAsync(stream, ct).ConfigureAwait(false);
                    if (request is not { } req)
                    {
                        break;
                    }

                    if (authenticated)
                    {
                        await WriteBodyAsync(stream, req.Method, req.Range, ct).ConfigureAwait(false);
                        continue;
                    }

                    string? authorization = req.Authorization;
                    if (authorization is null || !authorization.StartsWith("NTLM", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteChallengeAsync(stream, ntlmToken: null, ct).ConfigureAwait(false);
                        continue;
                    }

                    byte[] message = Convert.FromBase64String(authorization["NTLM".Length..].Trim());
                    int messageType = message.Length >= 12 ? BitConverter.ToInt32(message, 8) : 0;

                    if (messageType == 1)
                    {
                        serverChallenge = RandomNumberGenerator.GetBytes(8);
                        byte[] challenge = NtlmMessages.BuildChallenge(serverChallenge, TargetDomain, TargetComputer);
                        await WriteChallengeAsync(stream, Convert.ToBase64String(challenge), ct).ConfigureAwait(false);
                        continue;
                    }

                    if (messageType == 3)
                    {
                        (string domain, string username, byte[] ntResponse) = NtlmMessages.ParseAuthenticate(message);
                        bool cryptoOk = NtlmMessages.VerifyNtlmV2(domain, username, Password, serverChallenge, ntResponse);
                        authenticated = username.Equals(Username, StringComparison.OrdinalIgnoreCase) && cryptoOk;

                        if (!authenticated)
                        {
                            await WriteChallengeAsync(stream, ntlmToken: null, ct).ConfigureAwait(false);
                            continue;
                        }

                        await WriteBodyAsync(stream, req.Method, req.Range, ct).ConfigureAwait(false);
                        continue;
                    }

                    await WriteChallengeAsync(stream, ntlmToken: null, ct).ConfigureAwait(false);
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

    private static async Task WriteChallengeAsync(NetworkStream stream, string? ntlmToken, CancellationToken ct)
    {
        string challenge = ntlmToken is null ? "NTLM" : $"NTLM {ntlmToken}";
        byte[] head = Encoding.ASCII.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: {challenge}\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n"));
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
        headers.Append("Connection: keep-alive\r\n");

        byte[] head = Encoding.ASCII.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {reason}\r\n{headers}\r\n"));
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        if (!string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase) && slice.Length > 0)
        {
            await stream.WriteAsync(slice, ct).ConfigureAwait(false);
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private readonly record struct Request(string Method, string? Authorization, (long From, long? To)? Range);

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var text = new StringBuilder();
        while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal) && text.Length < 65536)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return null;
            }

            if (read <= 0)
            {
                return text.Length == 0 ? null : throw new IOException("Connection closed mid-request.");
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

        return new Request(method, authorization, range);
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
