using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Transport;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media.Hls;

/// <summary>
/// The HLS extractor (TASK-037 AC0). Recognises <c>.m3u8</c> URLs (by extension or the HLS content types),
/// fetches the playlist, and reports it as <see cref="MediaKind.Hls"/> — listing the variant qualities for
/// a master playlist so the quality selector can choose one. Runs before the generic progressive extractor.
/// A non-HLS URL or an unfetchable/unparseable playlist yields <see langword="null"/> so the registry moves
/// on (graceful degradation).
/// </summary>
internal sealed partial class HlsMediaExtractor : IMediaExtractor
{
    private static readonly string[] HlsContentTypes =
    [
        "application/vnd.apple.mpegurl",
        "application/x-mpegurl",
        "audio/mpegurl",
        "audio/x-mpegurl",
        "vnd.apple.mpegurl",
    ];

    private readonly ITransport _transport;
    private readonly ILogger<HlsMediaExtractor> _logger;

    public HlsMediaExtractor(ITransport transport, ILogger<HlsMediaExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(logger);
        _transport = transport;
        _logger = logger;
    }

    /// <summary>Runs before the generic progressive extractor.</summary>
    public int Priority => 100;

    public string Name => "hls";

    public async Task<MediaSource?> TryExtractAsync(
        MediaRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!LooksLikeHls(request))
        {
            return null;
        }

        string playlistText;
        try
        {
            playlistText = await FetchTextAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFetchFailed(_logger, request.Url, ex);
            return null;
        }

        if (!playlistText.Contains("#EXTM3U", StringComparison.Ordinal))
        {
            return null; // Not actually an HLS playlist despite the URL/content type.
        }

        if (M3U8Parser.IsMaster(playlistText))
        {
            HlsMasterPlaylist master = M3U8Parser.ParseMaster(playlistText, request.Url);
            IReadOnlyList<VideoVariant> variants = master.Variants
                .Select(v => new VideoVariant(v.Uri.ToString(), v.Height ?? 0, v.Bandwidth))
                .ToArray();

            return new MediaSource
            {
                ExtractorName = Name,
                Kind = MediaKind.Hls,
                Url = request.Url,
                SuggestedFileName = DeriveName(request.Url),
                Variants = variants,
            };
        }

        // A media playlist directly — a single quality, no variants to choose from.
        return new MediaSource
        {
            ExtractorName = Name,
            Kind = MediaKind.Hls,
            Url = request.Url,
            SuggestedFileName = DeriveName(request.Url),
        };
    }

    private static bool LooksLikeHls(MediaRequest request)
    {
        if (request.ContentType is { Length: > 0 } contentType)
        {
            foreach (string hls in HlsContentTypes)
            {
                if (contentType.Contains(hls, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        string extension = Path.GetExtension(request.Url.AbsolutePath);
        return extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".m3u", StringComparison.OrdinalIgnoreCase);
    }

    private static string? DeriveName(Uri url)
    {
        string name = Path.GetFileNameWithoutExtension(url.AbsolutePath);
        return string.IsNullOrEmpty(name) ? null : Uri.UnescapeDataString(name);
    }

    private async Task<string> FetchTextAsync(MediaRequest request, CancellationToken cancellationToken)
    {
        var transportRequest = new TransportRequest
        {
            Uri = request.Url,
            Method = TransportMethod.Get,
            Headers = request.Headers,
        };

        await using ITransportResponse response = await _transport.SendAsync(transportRequest, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HlsExtractionException($"Playlist fetch returned status {response.StatusCode}.");
        }

        await using Stream stream = await response.OpenContentStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Could not fetch HLS playlist {Url}; declining.")]
    private static partial void LogFetchFailed(ILogger logger, Uri url, Exception exception);
}
