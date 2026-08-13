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
/// <para>
/// When the playlist declares an <c>#EXT-X-MAP</c> initialization segment (fragmented-MP4/CMAF, as served by
/// Twitter/X and most modern CDNs) it is fetched first and returned as the first entry of
/// <see cref="HlsDownloadResult.SegmentFiles"/>, so the concatenated output starts with the
/// <c>ftyp</c>/<c>moov</c> boxes the fragments depend on. Without it the joined <c>.m4s</c> fragments decode
/// to nothing — ffprobe reports "trun track id unknown, no tfhd was found".
/// </para>
/// <para>
/// Segments carrying an <c>#EXT-X-BYTERANGE</c> sub-range are fetched with an HTTP <c>Range</c> header. A
/// server that ignores it and answers <c>200 OK</c> with the whole resource is tolerated by slicing the
/// requested window out locally — never by appending the full body, which would corrupt the output.
/// </para>
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

        // Fetched before the segments so a missing/unreachable init segment fails fast, rather than after
        // paying for the whole stream.
        string? initializationFile = null;
        long initializationBytes = 0;
        if (playlist.InitializationSegment is { } initializationSegment)
        {
            byte[] initialization = await FetchBytesAsync(
                    initializationSegment.Uri, headers, initializationSegment.ByteRange, cancellationToken)
                .ConfigureAwait(false);
            initializationFile = Path.Combine(workingDirectory, "init.mp4");
            await File.WriteAllBytesAsync(initializationFile, initialization, cancellationToken)
                .ConfigureAwait(false);
            initializationBytes = initialization.Length;
        }

        var keyCache = new ConcurrentDictionary<Uri, Task<byte[]>>();
        var segmentFiles = new string[playlist.Segments.Count];
        int totalSegments = playlist.Segments.Count;
        int completed = 0;
        long downloadedBytes = initializationBytes;

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
        string[] files = initializationFile is null ? segmentFiles : [initializationFile, .. segmentFiles];
        return new HlsDownloadResult(files, Interlocked.Read(ref downloadedBytes));
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
        byte[] cipher = await FetchBytesAsync(segment.Uri, headers, segment.ByteRange, cancellationToken)
            .ConfigureAwait(false);

        if (segment.Encryption.Method != HlsKeyMethod.Aes128)
        {
            return cipher;
        }

        // Fetch each distinct key exactly once; concurrent segments share the in-flight fetch.
        byte[] key = await keyCache.GetOrAdd(
            segment.Encryption.Uri!,
            uri => FetchBytesAsync(uri, headers, byteRange: null, cancellationToken)).ConfigureAwait(false);

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
        Uri uri,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        HlsByteRange? byteRange,
        CancellationToken cancellationToken)
    {
        var request = new TransportRequest
        {
            Uri = uri,
            Method = TransportMethod.Get,
            Headers = headers,
            Range = byteRange is { } requested ? new ByteRange(requested.Offset, requested.Last) : null,
        };

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
        byte[] body = buffer.ToArray();

        return byteRange is { } range ? ExtractRange(body, range, uri, response.IsPartialContent) : body;
    }

    /// <summary>
    /// Returns exactly the bytes of <paramref name="range"/>. A <c>206</c> body is used as-is once its length
    /// is confirmed; a <c>200</c> means the server ignored the <c>Range</c> header and sent the whole
    /// resource, so the window is sliced out locally rather than appended whole (which would corrupt the
    /// output). Anything that cannot contain the range fails loudly (CLAUDE.md §5, no silent failures).
    /// </summary>
    private byte[] ExtractRange(byte[] body, HlsByteRange range, Uri uri, bool isPartialContent)
    {
        if (isPartialContent)
        {
            if (body.LongLength != range.Length)
            {
                throw new HlsExtractionException(
                    $"'{uri}' answered the range request for {range.Length} bytes at offset {range.Offset} " +
                    $"with {body.LongLength} bytes.");
            }

            return body;
        }

        if (range.Offset + range.Length > body.LongLength)
        {
            throw new HlsExtractionException(
                $"'{uri}' ignored the Range header and returned {body.LongLength} bytes, which do not " +
                $"contain the requested sub-range {range.Length}@{range.Offset}.");
        }

        LogRangeIgnored(_logger, uri, range.Length, range.Offset);
        return body.AsSpan((int)range.Offset, (int)range.Length).ToArray();
    }

    private async Task<string> FetchTextAsync(
        Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await FetchBytesAsync(uri, headers, byteRange: null, cancellationToken).ConfigureAwait(false);
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

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "{Url} ignored the Range header; slicing {Length} bytes at offset {Offset} locally.")]
    private static partial void LogRangeIgnored(ILogger logger, Uri url, long length, long offset);
}
