using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace JustDownload.Tests.Transport;

/// <summary>
/// A minimal self-hosted static-file HTTP server for DASH fixtures (TASK-102): serves every file under
/// <see cref="RootDirectory"/> by its relative path — the on-disk layout ffmpeg's own DASH muxer produces
/// (a manifest plus its init/media segments) — so the real extractor/downloader/concatenator/muxer path can
/// be exercised end-to-end against a genuine SegmentTemplate/SegmentTimeline manifest. Built on
/// <see cref="TcpListener"/> (no admin rights needed) and torn down on <see cref="DisposeAsync"/>.
/// </summary>
internal sealed class LoopbackDashServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public LoopbackDashServer(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        RootDirectory = rootDirectory;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUri = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>The directory whose files are served, keyed by relative path.</summary>
    public string RootDirectory { get; }

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

                (byte[] body, int status, string contentType) = ReadFile(path);
                await WriteAsync(stream, body, status, contentType, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private (byte[] Body, int Status, string ContentType) ReadFile(string requestPath)
    {
        string relative = requestPath.TrimStart('/');
        string fullPath = Path.Combine(RootDirectory, relative);
        if (!File.Exists(fullPath))
        {
            return ([], 404, "application/octet-stream");
        }

        string contentType = Path.GetExtension(fullPath) switch
        {
            ".mpd" => "application/dash+xml",
            _ => "application/octet-stream",
        };

        return (File.ReadAllBytes(fullPath), 200, contentType);
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
        return parts.Length >= 2 ? Uri.UnescapeDataString(parts[1]) : null;
    }

    private static async Task WriteAsync(
        NetworkStream stream, byte[] body, int status, string contentType, CancellationToken ct)
    {
        string reason = status == 200 ? "OK" : "Not Found";
        byte[] head = Encoding.ASCII.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
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
