using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace JustDownload.Tests.Transport;

/// <summary>
/// A self-hosted HLS fixture served over real HTTP (TASK-082 AC2): a master playlist with one variant, a
/// media playlist of <c>.ts</c> segments, and — when <see cref="Encrypted"/> — an AES-128 key plus
/// segments encrypted on the wire (CBC/PKCS7, IV derived from the media sequence, matching RFC 8216). It
/// lets the real <c>HttpTransport</c> + HLS extractor/downloader be exercised end-to-end. Built on
/// <see cref="TcpListener"/> so it needs no admin rights and is torn down on <see cref="DisposeAsync"/>.
/// </summary>
internal sealed class LoopbackHlsServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly IReadOnlyList<byte[]> _plaintext;
    private readonly byte[] _key;

    public LoopbackHlsServer(IReadOnlyList<byte[]> plaintextSegments, bool encrypted)
    {
        ArgumentNullException.ThrowIfNull(plaintextSegments);
        _plaintext = plaintextSegments;
        Encrypted = encrypted;
        _key = RandomNumberGenerator.GetBytes(16);

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUri = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>Whether segments are AES-128 encrypted on the wire.</summary>
    public bool Encrypted { get; }

    /// <summary>The server base URL.</summary>
    public Uri BaseUri { get; }

    /// <summary>The master playlist URL (lists one variant).</summary>
    public Uri MasterUrl => new(BaseUri, "master.m3u8");

    /// <summary>The media playlist URL (the variant's segments).</summary>
    public Uri MediaUrl => new(BaseUri, "media.m3u8");

    /// <summary>The expected final bytes: the plaintext segments concatenated in order.</summary>
    public byte[] ReferenceBytes => _plaintext.SelectMany(s => s).ToArray();

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
                string? path = await ReadPathAsync(stream, ct).ConfigureAwait(false);
                if (path is null)
                {
                    return;
                }

                (byte[] body, string contentType) = Route(path);
                await WriteAsync(stream, body, contentType, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private (byte[] Body, string ContentType) Route(string path)
    {
        if (path.EndsWith("master.m3u8", StringComparison.Ordinal))
        {
            return (Encoding.UTF8.GetBytes(BuildMaster()), "application/vnd.apple.mpegurl");
        }

        if (path.EndsWith("media.m3u8", StringComparison.Ordinal))
        {
            return (Encoding.UTF8.GetBytes(BuildMedia()), "application/vnd.apple.mpegurl");
        }

        if (path.EndsWith("key.bin", StringComparison.Ordinal))
        {
            return (_key, "application/octet-stream");
        }

        int index = ParseSegmentIndex(path);
        if (index >= 0 && index < _plaintext.Count)
        {
            byte[] segment = Encrypted ? Encrypt(_plaintext[index], index) : _plaintext[index];
            return (segment, "video/mp2t");
        }

        return ([], "application/octet-stream");
    }

    private static string BuildMaster() =>
        "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\nmedia.m3u8\n";

    private string BuildMedia()
    {
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:0\n");
        if (Encrypted)
        {
            sb.Append("#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n");
        }

        for (int i = 0; i < _plaintext.Count; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"#EXTINF:6.0,\nseg{i}.ts\n");
        }

        sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    private byte[] Encrypt(byte[] plaintext, int mediaSequence)
    {
        byte[] iv = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(iv.AsSpan(8), (ulong)mediaSequence);
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using ICryptoTransform encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    private static int ParseSegmentIndex(string path)
    {
        int seg = path.LastIndexOf("seg", StringComparison.Ordinal);
        int dot = path.LastIndexOf(".ts", StringComparison.Ordinal);
        if (seg < 0 || dot <= seg + 3)
        {
            return -1;
        }

        return int.TryParse(path.AsSpan(seg + 3, dot - (seg + 3)), out int index) ? index : -1;
    }

    private static async Task<string?> ReadPathAsync(NetworkStream stream, CancellationToken ct)
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

        string first = text.ToString().Split("\r\n")[0];
        string[] parts = first.Split(' ');
        return parts.Length >= 2 ? parts[1] : null;
    }

    private static async Task WriteAsync(NetworkStream stream, byte[] body, string contentType, CancellationToken ct)
    {
        byte[] head = Encoding.ASCII.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
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
