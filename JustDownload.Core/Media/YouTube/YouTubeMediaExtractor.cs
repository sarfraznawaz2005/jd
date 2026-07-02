using System.Text.Json;
using System.Text.RegularExpressions;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Transport;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media.YouTube;

/// <summary>
/// Best-effort YouTube extractor (TASK-101, D3). Deliberately narrow in scope: modern YouTube protects
/// most playable URLs behind either a <c>signatureCipher</c>/<c>cipher</c> field or a bare <c>url</c> whose
/// <c>n</c> query parameter must be recomputed by evaluating an obfuscated function from YouTube's player
/// JS bundle — reverse-engineering that cipher is explicitly out of scope here (it is the reason yt-dlp
/// needs near-weekly maintenance; D3 calls for best-effort, not an adversarial cat-and-mouse game). This
/// extractor therefore only ever accepts a <c>streamingData.formats</c> entry that is already a complete,
/// playable URL: no <c>signatureCipher</c>/<c>cipher</c>, and no <c>n</c> parameter to resolve. It does not
/// attempt <c>adaptiveFormats</c> (separate video/audio) — as of TASK-101's research, those no longer carry
/// a direct URL at all on live YouTube (superseded by the server-side "SABR" streaming protocol), only
/// <c>formats</c> (progressive, always muxed) is a legacy path that can still occasionally appear
/// unciphered/unthrottled, and only a muxed URL is safe to report as <see cref="MediaKind.Progressive"/>
/// without silently dropping audio.
/// <para>
/// Empirically (see TASK-101 notes), most real YouTube videos today expose zero usable formats under this
/// honest scope — that is the expected, correct outcome per D3's graceful-degradation contract, not a bug.
/// </para>
/// </summary>
internal sealed partial class YouTubeMediaExtractor : IMediaExtractor
{
    private readonly ITransport _transport;
    private readonly ILogger<YouTubeMediaExtractor> _logger;

    public YouTubeMediaExtractor(ITransport transport, ILogger<YouTubeMediaExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(logger);
        _transport = transport;
        _logger = logger;
    }

    /// <summary>Runs before the generic progressive extractor.</summary>
    public int Priority => 90;

    public string Name => "youtube";

    public async Task<MediaSource?> TryExtractAsync(
        MediaRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!LooksLikeYouTube(request.Url))
        {
            return null;
        }

        string? videoId = TryGetVideoId(request.Url);
        if (videoId is null)
        {
            LogNoVideoId(_logger, request.Url);
            return null;
        }

        var watchUrl = new Uri($"https://www.youtube.com/watch?v={videoId}");
        string? html;
        try
        {
            html = await FetchTextAsync(request, watchUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            LogFetchFailed(_logger, watchUrl, ex);
            return null;
        }

        string? playerResponseJson = ExtractBracedJson(html, "ytInitialPlayerResponse");
        if (playerResponseJson is null)
        {
            LogNoPlayerResponse(_logger, watchUrl);
            return null;
        }

        string? mediaUrl;
        try
        {
            mediaUrl = FindUsableFormatUrl(playerResponseJson);
        }
        catch (JsonException ex)
        {
            LogParseFailed(_logger, watchUrl, ex);
            return null;
        }

        if (mediaUrl is null)
        {
            LogNoUsableFormat(_logger, watchUrl);
            return null;
        }

        return new MediaSource
        {
            ExtractorName = Name,
            Kind = MediaKind.Progressive,
            Url = new Uri(mediaUrl),
            SuggestedFileName = $"youtube-{videoId}",
        };
    }

    private static bool LooksLikeYouTube(Uri url) =>
        url.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        url.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetVideoId(Uri url)
    {
        Match match = WatchVParamRegex().Match(url.Query);
        if (!match.Success)
        {
            match = PathVideoIdRegex().Match(url.AbsoluteUri);
        }

        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Finds <paramref name="marker"/>'s assigned object literal (e.g. <c>ytInitialPlayerResponse = {...}</c>)
    /// via a brace-counting scan rather than a regex, so nested braces inside the JSON (and braces that
    /// happen to appear inside quoted strings) don't truncate the match.
    /// </summary>
    private static string? ExtractBracedJson(string html, string marker)
    {
        int markerIndex = html.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        int start = html.IndexOf('{', markerIndex);
        if (start < 0)
        {
            return null;
        }

        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int i = start; i < html.Length; i++)
        {
            char c = html[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return html[start..(i + 1)];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Scans <c>streamingData.formats</c> for the first entry with a bare, unciphered, unthrottled URL
    /// (see the type doc for the scope rationale). Returns <see langword="null"/> when none qualifies.
    /// </summary>
    private static string? FindUsableFormatUrl(string playerResponseJson)
    {
        using JsonDocument document = JsonDocument.Parse(playerResponseJson);

        if (!document.RootElement.TryGetProperty("streamingData", out JsonElement streamingData) ||
            !streamingData.TryGetProperty("formats", out JsonElement formats) ||
            formats.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement format in formats.EnumerateArray())
        {
            if (format.TryGetProperty("signatureCipher", out _) || format.TryGetProperty("cipher", out _))
            {
                continue; // Requires the obfuscated player-JS cipher — out of scope.
            }

            if (!format.TryGetProperty("url", out JsonElement urlProperty))
            {
                continue;
            }

            string? url = urlProperty.GetString();
            if (string.IsNullOrEmpty(url))
            {
                continue;
            }

            if (NParamRegex().IsMatch(url))
            {
                continue; // Requires the "n" throttling parameter to be recomputed — out of scope.
            }

            return url;
        }

        return null;
    }

    private async Task<string> FetchTextAsync(MediaRequest request, Uri url, CancellationToken cancellationToken)
    {
        var transportRequest = new TransportRequest
        {
            Uri = url,
            Method = TransportMethod.Get,
            Headers = request.Headers,
        };

        await using ITransportResponse response = await _transport.SendAsync(transportRequest, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Watch page fetch returned status {response.StatusCode}.");
        }

        await using Stream stream = await response.OpenContentStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex(@"[?&]v=([A-Za-z0-9_-]{11})", RegexOptions.CultureInvariant)]
    private static partial Regex WatchVParamRegex();

    [GeneratedRegex(@"(?:youtu\.be/|/shorts/|/embed/|/live/)([A-Za-z0-9_-]{11})", RegexOptions.CultureInvariant)]
    private static partial Regex PathVideoIdRegex();

    [GeneratedRegex(@"[?&]n=", RegexOptions.CultureInvariant)]
    private static partial Regex NParamRegex();

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Could not fetch YouTube watch page {Url}; declining.")]
    private static partial void LogFetchFailed(ILogger logger, Uri url, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "No ytInitialPlayerResponse found at {Url}; declining.")]
    private static partial void LogNoPlayerResponse(ILogger logger, Uri url);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Could not parse the player response at {Url}; declining.")]
    private static partial void LogParseFailed(ILogger logger, Uri url, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "No unciphered/unthrottled format available at {Url}; declining.")]
    private static partial void LogNoUsableFormat(ILogger logger, Uri url);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Could not resolve a YouTube video id for {Url}; declining.")]
    private static partial void LogNoVideoId(ILogger logger, Uri url);
}
