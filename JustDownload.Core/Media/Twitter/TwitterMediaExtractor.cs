using System.Text.Json;
using System.Text.RegularExpressions;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Media.Hls;
using JustDownload.Core.Transport;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media.Twitter;

/// <summary>
/// Best-effort Twitter/X extractor (D3, in-house). Recognises an <c>x.com</c>/<c>twitter.com</c>
/// <c>/status/&lt;id&gt;</c> URL, then reads the public, unauthenticated syndication API that Twitter's own
/// "Embedded Tweet" widget consumes (<c>cdn.syndication.twimg.com/tweet-result</c>) — the same data
/// Vercel's MIT <c>react-tweet</c> and yt-dlp's 429-fallback rely on. This is legitimate public embed data,
/// not an evasion of site protections (D3), and CORS is irrelevant because Core calls it server-side via
/// <see cref="ITransport"/>, never from a browser.
/// <para>
/// Every unavailable case — HTTP 404, an empty <c>{}</c> body, or a <c>TweetTombstone</c> (protected /
/// age-restricted / deleted) — degrades to <see langword="null"/> so the registry moves on to the generic
/// extractors (and ultimately yt-dlp at <c>Priority = int.MaxValue</c>). It never throws for "unavailable"
/// and never fabricates a result: only <see cref="OperationCanceledException"/> propagates.
/// </para>
/// </summary>
internal sealed partial class TwitterMediaExtractor : IMediaExtractor
{
    private readonly ITransport _transport;
    private readonly ILogger<TwitterMediaExtractor> _logger;

    public TwitterMediaExtractor(ITransport transport, ILogger<TwitterMediaExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(logger);
        _transport = transport;
        _logger = logger;
    }

    /// <summary>Runs before the generic progressive extractor (same band as Facebook/YouTube).</summary>
    public int Priority => 91;

    public string Name => "twitter";

    public async Task<MediaSource?> TryExtractAsync(
        MediaRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!LooksLikeTwitter(request.Url))
        {
            return null;
        }

        string? tweetId = TryGetTweetId(request.Url.AbsoluteUri);
        if (tweetId is null)
        {
            LogNoTweetId(_logger, request.Url);
            return null;
        }

        // Deterministic token (see TwitterSyndicationToken). Computed twice is identical, but the retry
        // below recomputes it to honour the "freshly computed token" contract on a transient empty body.
        string token = TwitterSyndicationToken.Generate(tweetId);
        (bool ok, string? json) = await TryFetchAsync(request, tweetId, token, cancellationToken)
            .ConfigureAwait(false);
        if (!ok)
        {
            return null; // 404 / network failure — decline, let another extractor (yt-dlp) try.
        }

        // A non-404 but empty/transient body: retry once with a freshly computed (still deterministic)
        // token before giving up. A successfully parsed tombstone/malformed/no-media outcome is NOT retried.
        if (IsTransientEmpty(json))
        {
            token = TwitterSyndicationToken.Generate(tweetId);
            (ok, json) = await TryFetchAsync(request, tweetId, token, cancellationToken).ConfigureAwait(false);
            if (!ok || IsTransientEmpty(json))
            {
                return null;
            }
        }

        return await TryBuildFromJsonAsync(request, tweetId, json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses the syndication JSON (tolerant — a missing field is skipped, malformed JSON returns
    /// <see langword="null"/>) and builds a <see cref="MediaSource"/>. Prefers the HLS master playlist when
    /// present (more robust per react-tweet issue #191), else the highest-bitrate MP4. When the HLS URL is a
    /// master playlist, its real quality variants are fetched and parsed (mirroring <c>HlsMediaExtractor</c>);
    /// a fetch failure or a media (non-master) playlist degrades to an empty variant list rather than failing
    /// the whole extraction — the master/media URL is still a valid single-stream download either way.
    /// </summary>
    private async Task<MediaSource?> TryBuildFromJsonAsync(
        MediaRequest request, string tweetId, string? json, CancellationToken cancellationToken)
    {
        if (IsTransientEmpty(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json!);
            JsonElement root = document.RootElement;

            // Protected / age-restricted / removed tweet — decline gracefully so yt-dlp can fall back.
            if (TryGetProperty(root, "__typename", out JsonElement typename) &&
                typename.ValueKind == JsonValueKind.String &&
                string.Equals(typename.GetString(), "TweetTombstone", StringComparison.OrdinalIgnoreCase))
            {
                LogTombstone(_logger, tweetId);
                return null;
            }

            string? text = TryGetProperty(root, "text", out JsonElement textEl) &&
                            textEl.ValueKind == JsonValueKind.String
                ? textEl.GetString()
                : null;

            var variants = new List<(string ContentType, string Url, long Bitrate)>();

            // Legacy v1.1 shape: mediaDetails[] -> video_info.variants[].
            if (TryGetProperty(root, "mediaDetails", out JsonElement mediaDetails) &&
                mediaDetails.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in mediaDetails.EnumerateArray())
                {
                    if (!TryGetProperty(item, "type", out JsonElement typeEl) ||
                        typeEl.ValueKind != JsonValueKind.String ||
                        !string.Equals(typeEl.GetString(), "video", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (TryGetProperty(item, "video_info", out JsonElement videoInfo) &&
                        TryGetProperty(videoInfo, "variants", out JsonElement variantList) &&
                        variantList.ValueKind == JsonValueKind.Array)
                    {
                        CollectVariants(variantList, variants);
                    }
                }
            }

            // Normalized shape: video.variants[] with { type (content-type), src (url) }.
            if (TryGetProperty(root, "video", out JsonElement video) &&
                video.ValueKind == JsonValueKind.Object &&
                TryGetProperty(video, "variants", out JsonElement videoVariants) &&
                videoVariants.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement v in videoVariants.EnumerateArray())
                {
                    if (!TryGetProperty(v, "type", out JsonElement ctEl) ||
                        ctEl.ValueKind != JsonValueKind.String ||
                        !TryGetProperty(v, "src", out JsonElement srcEl) ||
                        srcEl.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string contentType = ctEl.GetString()!;
                    string url = srcEl.GetString()!;
                    long bitrate = TryGetProperty(v, "bitrate", out JsonElement b) && b.ValueKind == JsonValueKind.Number
                        ? b.GetInt64()
                        : 0;
                    variants.Add((contentType, url, bitrate));
                }
            }

            if (variants.Count == 0)
            {
                return null; // Tweet genuinely has no extractable video.
            }

            // Prefer the HLS master.
            foreach ((string contentType, string url, _) in variants)
            {
                if (string.Equals(contentType, "application/x-mpegURL", StringComparison.OrdinalIgnoreCase) &&
                    Uri.TryCreate(url, UriKind.Absolute, out Uri? hlsUri))
                {
                    (IReadOnlyList<VideoVariant> hlsVariants, IReadOnlyList<AudioVariant> hlsAudioVariants) =
                        await TryFetchHlsVariantsAsync(request, hlsUri, cancellationToken).ConfigureAwait(false);

                    return new MediaSource
                    {
                        ExtractorName = Name,
                        Kind = MediaKind.Hls,
                        Url = hlsUri,
                        SuggestedFileName = CrossPlatformFileName.Sanitize(text) ?? $"twitter-{tweetId}",
                        Variants = hlsVariants,
                        AudioVariants = hlsAudioVariants,
                    };
                }
            }

            // Else the highest-bitrate MP4 (heights are not reliably present — do not invent them).
            (string Url, long Bitrate)? bestMp4 = null;
            foreach ((string contentType, string url, long bitrate) in variants)
            {
                if (!string.Equals(contentType, "video/mp4", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (bestMp4 is null || bitrate > bestMp4.Value.Bitrate)
                {
                    bestMp4 = (url, bitrate);
                }
            }

            if (bestMp4 is not null &&
                Uri.TryCreate(bestMp4.Value.Url, UriKind.Absolute, out Uri? mp4Uri))
            {
                return new MediaSource
                {
                    ExtractorName = Name,
                    Kind = MediaKind.Progressive,
                    Url = mp4Uri,
                    SuggestedFileName = CrossPlatformFileName.Sanitize(text) ?? $"twitter-{tweetId}",
                    Variants = [],
                    AudioVariants = [],
                };
            }

            return null;
        }
        catch (JsonException)
        {
            return null; // Malformed JSON — decline rather than throw.
        }
    }

    private static void CollectVariants(
        JsonElement variants, List<(string ContentType, string Url, long Bitrate)> outList)
    {
        foreach (JsonElement v in variants.EnumerateArray())
        {
            if (!TryGetProperty(v, "content_type", out JsonElement ctEl) ||
                ctEl.ValueKind != JsonValueKind.String ||
                !TryGetProperty(v, "url", out JsonElement urlEl) ||
                urlEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string contentType = ctEl.GetString()!;
            string url = urlEl.GetString()!;
            long bitrate = TryGetProperty(v, "bitrate", out JsonElement b) && b.ValueKind == JsonValueKind.Number
                ? b.GetInt64()
                : 0;
            outList.Add((contentType, url, bitrate));
        }
    }

    private static bool IsTransientEmpty(string? json) =>
        string.IsNullOrWhiteSpace(json) || json!.Trim() == "{}";

    private static bool LooksLikeTwitter(Uri url) =>
        IsTwitterHost(url.Host) && StatusRegex().IsMatch(url.AbsoluteUri);

    private static bool IsTwitterHost(string host) =>
        host.Equals("x.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".x.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("twitter.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".twitter.com", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetTweetId(string url)
    {
        Match match = StatusRegex().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Case-insensitive JSON property lookup (the syndication JSON mixes <c>camelCase</c> and
    /// <c>snake_case</c> fields), so a missing/mis-cased field is treated as absent rather than throwing.
    /// </summary>
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private async Task<(bool Ok, string? Body)> TryFetchAsync(
        MediaRequest request, string tweetId, string token, CancellationToken cancellationToken)
    {
        var url = new Uri(
            $"https://cdn.syndication.twimg.com/tweet-result?id={tweetId}&token={token}&lang=en");

        try
        {
            var transportRequest = new TransportRequest
            {
                Uri = url,
                Method = TransportMethod.Get,
                Headers = request.Headers,
            };

            await using ITransportResponse response = await _transport
                .SendAsync(transportRequest, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LogFetchFailed(_logger, url, response.StatusCode);
                return (false, null);
            }

            await using Stream stream = await response.OpenContentStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return (true, text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            LogFetchException(_logger, url, ex);
            return (false, null);
        }
    }

    /// <summary>
    /// Fetches <paramref name="hlsUri"/> and, if it is a master playlist, parses its real quality variants
    /// and any alternate audio renditions (<c>#EXT-X-MEDIA:TYPE=AUDIO</c> — mirrors <see cref="HlsMediaExtractor"/>'s
    /// own master-playlist parsing plus the RFC 8216 §4.3.4.1 alternate-audio case, since a Twitter/X master
    /// playlist can carry audio either muxed into each video variant or as its own downloadable rendition).
    /// Degrades to empty lists — never throws, never fails the caller — for a fetch failure (network
    /// exception, non-2xx), a body that isn't recognisably HLS, or a media (non-master) playlist; the
    /// master/media URL itself remains a valid single-stream download either way.
    /// </summary>
    private async Task<(IReadOnlyList<VideoVariant> Variants, IReadOnlyList<AudioVariant> AudioVariants)>
        TryFetchHlsVariantsAsync(MediaRequest request, Uri hlsUri, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            var transportRequest = new TransportRequest
            {
                Uri = hlsUri,
                Method = TransportMethod.Get,
                Headers = request.Headers,
            };

            await using ITransportResponse response = await _transport
                .SendAsync(transportRequest, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LogHlsMasterFetchFailed(_logger, hlsUri, response.StatusCode);
                return ([], []);
            }

            await using Stream stream = await response.OpenContentStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            LogHlsMasterFetchException(_logger, hlsUri, ex);
            return ([], []);
        }

        if (!text.Contains("#EXTM3U", StringComparison.Ordinal) || !M3U8Parser.IsMaster(text))
        {
            return ([], []);
        }

        HlsMasterPlaylist master = M3U8Parser.ParseMaster(text, hlsUri);
        IReadOnlyList<VideoVariant> variants = master.Variants
            .Select(v => new VideoVariant(v.Uri.ToString(), v.Height ?? 0, v.Bandwidth))
            .ToArray();
        IReadOnlyList<AudioVariant> audioVariants = master.AudioRenditions
            .Select(a => new AudioVariant(a.Uri.ToString(), Language: a.Language))
            .ToArray();

        return (variants, audioVariants);
    }

    // The tweet id is the digit run immediately after /status/, ignoring trailing /video/N, /photo/N and
    // query strings — the first match is always the id.
    [GeneratedRegex(@"/status/(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex StatusRegex();

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Twitter fetch of {Url} returned status {StatusCode}; declining.")]
    private static partial void LogFetchFailed(ILogger logger, Uri url, int statusCode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Could not fetch Twitter syndication for {Url}; declining.")]
    private static partial void LogFetchException(ILogger logger, Uri url, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Could not resolve a Twitter tweet id for {Url}; declining.")]
    private static partial void LogNoTweetId(ILogger logger, Uri url);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Twitter tweet {TweetId} is a tombstone (protected/age-restricted/removed); declining.")]
    private static partial void LogTombstone(ILogger logger, string tweetId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Twitter HLS master fetch of {Url} returned status {StatusCode}; returning it without parsed variants.")]
    private static partial void LogHlsMasterFetchFailed(ILogger logger, Uri url, int statusCode);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "Could not fetch Twitter HLS master playlist {Url}; returning it without parsed variants.")]
    private static partial void LogHlsMasterFetchException(ILogger logger, Uri url, Exception exception);
}
