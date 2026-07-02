using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace JustDownload.Tests.Transport;

/// <summary>
/// A minimal real FTP/FTPS server for exercising <see cref="JustDownload.Core.Transport.Ftp.FluentFtpConnection"/>
/// over real sockets (TASK-111): a raw <see cref="TcpListener"/> control-channel state machine (USER/PASS,
/// FEAT, PASV/EPSV, SIZE, REST, RETR, QUIT) plus a passive-mode data connection per transfer, and an explicit
/// <c>AUTH TLS</c> upgrade (via <see cref="SslStream"/> and a runtime-generated self-signed certificate) for
/// FTPS. Each control connection is served independently so segmented downloads (one connection per segment)
/// work exactly as against a real server. Unimplemented commands answer <c>502</c>, which real FTP clients
/// tolerate as "feature not available".
/// </summary>
internal sealed class LoopbackFtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly X509Certificate2? _certificate;

    public LoopbackFtpServer(string username, string password, string path, byte[] body, bool requireTls = false)
    {
        Username = username;
        Password = password;
        Path = path;
        Body = body;
        RequireTls = requireTls;
        _certificate = requireTls ? CreateSelfSignedCertificate() : null;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public string Username { get; }

    public string Password { get; }

    /// <summary>The absolute path RETR/SIZE recognise (must match the download URL's path exactly).</summary>
    public string Path { get; }

    public byte[] Body { get; }

    public bool RequireTls { get; }

    public int Port { get; }

    /// <summary>The <c>ftp://</c>/<c>ftps://</c> URL for <see cref="Path"/>, with credentials embedded.</summary>
    public Uri Url() => new(string.Create(
        CultureInfo.InvariantCulture,
        $"{(RequireTls ? "ftps" : "ftp")}://{Username}:{Password}@127.0.0.1:{Port}{Path}"));

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

            _ = HandleControlConnectionAsync(client, ct);
        }
    }

    private async Task HandleControlConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            Stream stream = client.GetStream();
            TcpListener? dataListener = null;
            try
            {
                await WriteLineAsync(stream, "220 JustDownload test FTP server ready.", ct).ConfigureAwait(false);

                string? lastUser = null;
                long restOffset = 0;
                bool dataProtected = false;

                while (true)
                {
                    string? line = await ReadLineAsync(stream, ct).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    (string verb, string arg) = SplitCommand(line);
                    switch (verb.ToUpperInvariant())
                    {
                        case "USER":
                            lastUser = arg;
                            await WriteLineAsync(stream, "331 Password required.", ct).ConfigureAwait(false);
                            break;

                        case "PASS":
                            bool ok = lastUser == Username && arg == Password;
                            await WriteLineAsync(
                                stream, ok ? "230 User logged in." : "530 Login incorrect.", ct)
                                .ConfigureAwait(false);
                            break;

                        case "SYST":
                            await WriteLineAsync(stream, "215 UNIX Type: L8", ct).ConfigureAwait(false);
                            break;

                        case "FEAT":
                            await WriteLineAsync(stream, "211-Features:", ct).ConfigureAwait(false);
                            await WriteLineAsync(stream, " SIZE", ct).ConfigureAwait(false);
                            await WriteLineAsync(stream, " REST STREAM", ct).ConfigureAwait(false);
                            await WriteLineAsync(stream, " UTF8", ct).ConfigureAwait(false);
                            if (RequireTls)
                            {
                                await WriteLineAsync(stream, " AUTH TLS", ct).ConfigureAwait(false);
                                await WriteLineAsync(stream, " PBSZ", ct).ConfigureAwait(false);
                                await WriteLineAsync(stream, " PROT", ct).ConfigureAwait(false);
                            }

                            await WriteLineAsync(stream, "211 End", ct).ConfigureAwait(false);
                            break;

                        case "PWD":
                            await WriteLineAsync(stream, "257 \"/\" is current directory.", ct).ConfigureAwait(false);
                            break;

                        case "CWD":
                            await WriteLineAsync(stream, "250 Directory changed.", ct).ConfigureAwait(false);
                            break;

                        case "TYPE":
                            await WriteLineAsync(stream, "200 Type set.", ct).ConfigureAwait(false);
                            break;

                        case "OPTS":
                            await WriteLineAsync(stream, "200 Option accepted.", ct).ConfigureAwait(false);
                            break;

                        case "AUTH":
                            if (RequireTls && arg.Equals("TLS", StringComparison.OrdinalIgnoreCase))
                            {
                                await WriteLineAsync(stream, "234 AUTH TLS successful.", ct).ConfigureAwait(false);
                                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                                await ssl.AuthenticateAsServerAsync(
                                    new SslServerAuthenticationOptions
                                    {
                                        ServerCertificate = _certificate,
                                        EnabledSslProtocols = SslProtocols.None, // OS-negotiated best available
                                        ClientCertificateRequired = false,
                                    },
                                    ct).ConfigureAwait(false);
                                stream = ssl;
                            }
                            else
                            {
                                await WriteLineAsync(stream, "502 Command not implemented.", ct).ConfigureAwait(false);
                            }

                            break;

                        case "PBSZ":
                            await WriteLineAsync(stream, "200 PBSZ=0.", ct).ConfigureAwait(false);
                            break;

                        case "PROT":
                            dataProtected = arg.Equals("P", StringComparison.OrdinalIgnoreCase);
                            await WriteLineAsync(
                                stream, string.Create(CultureInfo.InvariantCulture, $"200 PROT set to {arg}."), ct)
                                .ConfigureAwait(false);
                            break;

                        case "SIZE":
                            await WriteLineAsync(
                                stream,
                                arg == Path
                                    ? string.Create(CultureInfo.InvariantCulture, $"213 {Body.Length}")
                                    : "550 File not found.",
                                ct).ConfigureAwait(false);
                            break;

                        case "REST":
                            restOffset = long.Parse(arg, CultureInfo.InvariantCulture);
                            await WriteLineAsync(
                                stream,
                                string.Create(CultureInfo.InvariantCulture, $"350 Restarting at {restOffset}."),
                                ct).ConfigureAwait(false);
                            break;

                        case "PASV":
                        case "EPSV":
                            dataListener?.Stop();
                            dataListener = new TcpListener(IPAddress.Loopback, 0);
                            dataListener.Start();
                            int dataPort = ((IPEndPoint)dataListener.LocalEndpoint).Port;
                            if (verb == "PASV")
                            {
                                int p1 = dataPort / 256, p2 = dataPort % 256;
                                await WriteLineAsync(
                                    stream,
                                    string.Create(
                                        CultureInfo.InvariantCulture,
                                        $"227 Entering Passive Mode (127,0,0,1,{p1},{p2})."),
                                    ct).ConfigureAwait(false);
                            }
                            else
                            {
                                await WriteLineAsync(
                                    stream,
                                    string.Create(
                                        CultureInfo.InvariantCulture,
                                        $"229 Entering Extended Passive Mode (|||{dataPort}|)."),
                                    ct).ConfigureAwait(false);
                            }

                            break;

                        case "RETR":
                            await HandleRetrAsync(
                                stream, dataListener, arg, restOffset, dataProtected, ct).ConfigureAwait(false);
                            dataListener?.Stop();
                            dataListener = null;
                            restOffset = 0;
                            break;

                        case "QUIT":
                            await WriteLineAsync(stream, "221 Goodbye.", ct).ConfigureAwait(false);
                            return;

                        default:
                            await WriteLineAsync(stream, "502 Command not implemented.", ct).ConfigureAwait(false);
                            break;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (AuthenticationException)
            {
                // TLS handshake rejected/aborted by the client (e.g. TASK-112's untrusted-certificate test).
            }
            finally
            {
                dataListener?.Stop();
                if (stream is SslStream ssl)
                {
                    await ssl.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task HandleRetrAsync(
        Stream controlStream,
        TcpListener? dataListener,
        string path,
        long restOffset,
        bool dataProtected,
        CancellationToken ct)
    {
        if (dataListener is null || path != Path)
        {
            await WriteLineAsync(controlStream, "550 File not found.", ct).ConfigureAwait(false);
            return;
        }

        await WriteLineAsync(
            controlStream,
            string.Create(CultureInfo.InvariantCulture, $"150 Opening BINARY data connection for {path}."),
            ct).ConfigureAwait(false);

        using TcpClient dataClient = await dataListener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
        Stream dataStream = dataClient.GetStream();
        SslStream? dataSsl = null;
        try
        {
            if (dataProtected && RequireTls)
            {
                dataSsl = new SslStream(dataStream, leaveInnerStreamOpen: false);
                await dataSsl.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        EnabledSslProtocols = SslProtocols.None,
                        ClientCertificateRequired = false,
                    },
                    ct).ConfigureAwait(false);
                dataStream = dataSsl;
            }

            long from = Math.Clamp(restOffset, 0, Body.Length);
            if (from < Body.Length)
            {
                await dataStream.WriteAsync(Body.AsMemory((int)from), ct).ConfigureAwait(false);
            }

            await dataStream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (dataSsl is not null)
            {
                await dataSsl.DisposeAsync().ConfigureAwait(false);
            }
        }

        await WriteLineAsync(controlStream, "226 Transfer complete.", ct).ConfigureAwait(false);
    }

    private static (string Verb, string Arg) SplitCommand(string line)
    {
        int space = line.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (line, string.Empty) : (line[..space], line[(space + 1)..].Trim());
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var line = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return null;
            }

            if (read <= 0)
            {
                return line.Count == 0 ? null : Encoding.ASCII.GetString([.. line]).TrimEnd('\r');
            }

            if (one[0] == (byte)'\n')
            {
                return Encoding.ASCII.GetString([.. line]).TrimEnd('\r');
            }

            line.Add(one[0]);
        }
    }

    private static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(line + "\r\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A runtime-generated, unsigned self-signed certificate for the FTPS <c>AUTH TLS</c> upgrade — test-only,
    /// never installed into any trust store, so <see cref="FluentFTP.FtpConfig.ValidateAnyCertificate"/> being
    /// <see langword="false"/> (the safe default, TASK-112) correctly rejects it via normal chain validation.
    /// </summary>
    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());

        using X509Certificate2 selfSigned =
            request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Re-import from PFX so the private key is usable by SslStream across platforms.
#pragma warning disable SYSLIB0057 // net8.0 target: the replacement X509CertificateLoader API is net9.0+ only.
        return new X509Certificate2(selfSigned.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
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
        _certificate?.Dispose();
    }
}
