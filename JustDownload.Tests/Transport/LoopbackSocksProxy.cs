using System.Net;
using System.Net.Sockets;
using System.Text;

namespace JustDownload.Tests.Transport;

/// <summary>
/// A minimal in-process SOCKS5 proxy for routing tests (TASK-115). It performs the no-auth SOCKS5 handshake
/// .NET's <see cref="System.Net.Http.SocketsHttpHandler"/> speaks for a <c>socks5://</c> proxy, records each
/// CONNECT target (so a test can prove traffic was actually tunnelled through it), connects to the requested
/// host/port, and relays bytes both ways until either side closes. Loopback only; torn down on
/// <see cref="DisposeAsync"/>.
/// </summary>
internal sealed class LoopbackSocksProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly object _gate = new();
    private readonly List<string> _targets = [];

    public LoopbackSocksProxy()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>The loopback port the proxy listens on.</summary>
    public int Port { get; }

    /// <summary>The CONNECT targets (<c>host:port</c>) the proxy was asked to tunnel to.</summary>
    public IReadOnlyList<string> ConnectedTargets
    {
        get
        {
            lock (_gate)
            {
                return _targets.ToArray();
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = HandleAsync(client, ct);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                NetworkStream cs = client.GetStream();

                // Greeting: VER, NMETHODS, METHODS[NMETHODS]. Reply: VER=5, METHOD=0 (no auth).
                var greeting = new byte[2];
                await cs.ReadExactlyAsync(greeting, ct).ConfigureAwait(false);
                await cs.ReadExactlyAsync(new byte[greeting[1]], ct).ConfigureAwait(false);
                await cs.WriteAsync(new byte[] { 0x05, 0x00 }, ct).ConfigureAwait(false);

                // Request: VER, CMD(1=CONNECT), RSV, ATYP, DST.ADDR, DST.PORT.
                var head = new byte[4];
                await cs.ReadExactlyAsync(head, ct).ConfigureAwait(false);
                string host = await ReadAddressAsync(cs, head[3], ct).ConfigureAwait(false);
                var portBytes = new byte[2];
                await cs.ReadExactlyAsync(portBytes, ct).ConfigureAwait(false);
                int port = (portBytes[0] << 8) | portBytes[1];

                lock (_gate)
                {
                    _targets.Add($"{host}:{port}");
                }

                using var target = new TcpClient();
                await target.ConnectAsync(host, port, ct).ConfigureAwait(false);

                // Reply: VER=5, REP=0 (success), RSV, ATYP=1, BND.ADDR=0.0.0.0, BND.PORT=0.
                await cs.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct).ConfigureAwait(false);

                NetworkStream ts = target.GetStream();
                Task up = cs.CopyToAsync(ts, ct);
                Task down = ts.CopyToAsync(cs, ct);
                await Task.WhenAny(up, down).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // A closed connection / cancelled tunnel — nothing to do.
        }
    }

    private static async Task<string> ReadAddressAsync(NetworkStream stream, byte atyp, CancellationToken ct)
    {
        switch (atyp)
        {
            case 0x01: // IPv4
                var v4 = new byte[4];
                await stream.ReadExactlyAsync(v4, ct).ConfigureAwait(false);
                return new IPAddress(v4).ToString();
            case 0x04: // IPv6
                var v6 = new byte[16];
                await stream.ReadExactlyAsync(v6, ct).ConfigureAwait(false);
                return new IPAddress(v6).ToString();
            case 0x03: // domain name
                var len = new byte[1];
                await stream.ReadExactlyAsync(len, ct).ConfigureAwait(false);
                var name = new byte[len[0]];
                await stream.ReadExactlyAsync(name, ct).ConfigureAwait(false);
                return Encoding.ASCII.GetString(name);
            default:
                throw new IOException($"Unsupported SOCKS5 address type {atyp}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort teardown.
        }

        _cts.Dispose();
    }
}
