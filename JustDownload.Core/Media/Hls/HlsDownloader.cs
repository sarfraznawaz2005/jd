using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using JustDownload.Core.Transport;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media.Hls;

/// <summary>
/// Default <see cref="IHlsDownloader"/> (TASK-037). Fetches the media playlist over the shared
/// <see cref="ITransport"/>, downloads every segment with bounded parallelism (AC1), decrypts AES-128
/// segments — fetching each distinct key once and caching it, deriving the IV from the media sequence when
/// the key tag omits one (RFC 8216 §5.2, AC2) — and reports segment-count progress (AC3). Decrypted
/// segments are written in playlist order as <c>seg00000.ts</c>, <c>seg00001.ts</c>, … ready for concat
/// (TASK-038). Cancellation is honoured promptly.
/// </summary>
internal sealed partial class HlsDownloader : IHlsDownloader
{
    private readonly ITransport _transport;
    private readonly HlsOptions _options;
    private readonly ILogger<HlsDownloader> _logger;

    public HlsDownloader(ITransport transport, HlsOptions options, ILogger<HlsDownloader> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _transport = transport;
        _options = options;
        _logger = logger;
    }

    public async Task<HlsDownloadResult> DownloadAsync(
        Uri mediaPlaylistUri,
        string workingDirectory,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null,
        IProgress<HlsProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaPlaylistUri);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        headers ??= [];
        Directory.CreateDirectory(workingDirectory);

        string playlistText = await FetchTextAsync(mediaPlaylistUri, headers, cancellationToken)
            .ConfigureAwait(false);

        if (M3U8Parser.IsMaster(playlistText))
        {
            throw new HlsExtractionException(
                "A master playlist was supplied; select a variant first and download its media playlist.");
        }

        HlsMediaPlaylist playlist = M3U8Parser.ParseMedia(playlistText, mediaPlaylistUri);
        if (playlist.Segments.Count == 0)
        {
            throw new HlsExtractionException("The HLS media playlist contains no segments.");
        }

        EnsureSupportedEncryption(playlist);

        var keyCache = new ConcurrentDictionary<Uri, Task<byte[]>>();
        var segmentFiles = new string[playlist.Segments.Count];
        int totalSegments = playlist.Segments.Count;
        int completed = 0;
        long downloadedBytes = 0;

        using var throttle = new SemaphoreSlim(Math.Max(1, _options.MaxParallelSegments));
        var tasks = new List<Task>(totalSegments);

        for (int index = 0; index < playlist.Segments.Count; index++)
        {
            int segmentIndex = index;
            HlsSegment segment = playlist.Segments[segmentIndex];

            tasks.Add(Task.Run(
                async () =>
                {
                    await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        byte[] data = await DownloadSegmentAsync(segment, headers, keyCache, cancellationToken)
                            .ConfigureAwait(false);

                        string path = Path.Combine(
                            workingDirectory,
                            string.Create(CultureInfo.InvariantCulture, $"seg{segmentIndex:D5}.ts"));
                        await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
                        segmentFiles[segmentIndex] = path;

                        int done = Interlocked.Increment(ref completed);
                        long bytes = Interlocked.Add(ref downloadedBytes, data.Length);
                        progress?.Report(new HlsProgress(done, totalSegments, bytes));
                    }
                    finally
                    {
                        throttle.Release();
                    }
                },
                cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        LogDownloaded(_logger, totalSegments, mediaPlaylistUri);
        return new HlsDownloadResult(segmentFiles, Interlocked.Read(ref downloadedBytes));
    }

    private static void EnsureSupportedEncryption(HlsMediaPlaylist playlist)
    {
        foreach (HlsSegment segment in playlist.Segments)
        {
            if (segment.Encryption.Method == HlsKeyMethod.SampleAes)
            {
                throw new HlsExtractionException("SAMPLE-AES encrypted HLS is not supported.");
            }

            if (segment.Encryption.Method == HlsKeyMethod.Aes128 && segment.Encryption.Uri is null)
            {
                throw new HlsExtractionException("An AES-128 segment is missing its key URI.");
            }
        }
    }

    private async Task<byte[]> DownloadSegmentAsync(
        HlsSegment segment,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        ConcurrentDictionary<Uri, Task<byte[]>> keyCache,
        CancellationToken cancellationToken)
    {
        byte[] cipher = await FetchBytesAsync(segment.Uri, headers, cancellationToken).ConfigureAwait(false);

        if (segment.Encryption.Method != HlsKeyMethod.Aes128)
        {
            return cipher;
        }

        // Fetch each distinct key exactly once; concurrent segments share the in-flight fetch.
        byte[] key = await keyCache.GetOrAdd(
            segment.Encryption.Uri!,
            uri => FetchBytesAsync(uri, headers, cancellationToken)).ConfigureAwait(false);

        if (key.Length != 16)
        {
            throw new HlsExtractionException(
                $"AES-128 key from '{segment.Encryption.Uri}' is {key.Length} bytes; expected 16.");
        }

        byte[] iv = ResolveIv(segment);
        return Decrypt(cipher, key, iv);
    }

    private static byte[] ResolveIv(HlsSegment segment)
    {
        if (segment.Encryption.Iv is { Count: 16 } explicitIv)
        {
            return explicitIv.ToArray();
        }

        // No explicit IV: the 128-bit big-endian media sequence number is used (RFC 8216 §5.2).
        var iv = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(iv.AsSpan(8), (ulong)segment.MediaSequence);
        return iv;
    }

    private static byte[] Decrypt(byte[] cipher, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using ICryptoTransform decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
    }

    private async Task<byte[]> FetchBytesAsync(
        Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
    {
        var request = new TransportRequest { Uri = uri, Method = TransportMethod.Get, Headers = headers };
        await using ITransportResponse response = await _transport.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HlsExtractionException(
                $"Fetching '{uri}' failed with status {response.StatusCode}.");
        }

        await using Stream stream = await response.OpenContentStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private async Task<string> FetchTextAsync(
        Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await FetchBytesAsync(uri, headers, cancellationToken).ConfigureAwait(false);
        }
        catch (HlsExtractionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new HlsExtractionException($"Could not fetch the HLS playlist at '{uri}'.", ex);
        }

        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Downloaded {Count} HLS segments from {Url}.")]
    private static partial void LogDownloaded(ILogger logger, int count, Uri url);
}
