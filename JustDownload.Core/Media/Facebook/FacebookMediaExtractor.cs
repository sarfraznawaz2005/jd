using System.Text.Json;
using System.Text.RegularExpressions;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Transport;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media.Facebook;

/// <summary>
/// Best-effort Facebook extractor (TASK-101, D3). Facebook video pages embed the playable MP4 URLs
/// (<c>hd_src</c>/<c>sd_src</c>) directly, without cipher obfuscation — unlike YouTube (see
/// <c>YouTubeMediaExtractor</c>), so this is the tractable half of TASK-101. Recognises a
/// <c>facebook.com</c>/<c>fb.watch</c> URL, resolves it to a numeric video id (from the URL directly, or
/// by following the page and reading its embedded <c>video_id</c>), then fetches Facebook's public embed
/// endpoint and reads <c>hd_src</c>/<c>sd_src</c> — the same fields Facebook itself serves to any embedded
/// player. Every step degrades to <see langword="null"/> (private/removed/login-gated video, unexpected
/// page shape, network failure) so the registry moves on to the generic extractors — never throws, never
/// fakes success.
/// </summary>
internal sealed partial class FacebookMediaExtractor : IMediaExtractor
{
    private readonly ITransport _transport;
    private readonly ILogger<FacebookMediaExtractor> _logger;

    public FacebookMediaExtractor(ITransport transport, ILogger<FacebookMediaExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(logger);
        _transport = transport;
        _logger = logger;
    }

    /// <summary>Runs before the generic progressive extractor.</summary>
    public int Priority => 91;

    public string Name => "facebook";

    public async Task<MediaSource?> TryExtractAsync(
        MediaRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!LooksLikeFacebook(request.Url))
        {
            return null;
        }

        string? videoId = TryGetVideoId(request.Url.AbsoluteUri);

        if (videoId is null)
        {
            // Opaque short links (fb.watch, /share/v/...) carry no id — follow the page and read its
            // embedded video_id instead.
            (string? pageHtml, Uri? finalUri) = await TryFetchAsync(request, request.Url, cancellationToken)
                .ConfigureAwait(false);
            if (pageHtml is null)
            {
                return null;
            }

            videoId = (finalUri is not null ? TryGetVideoId(finalUri.AbsoluteUri) : null)
                ?? ExtractJsonStringField(pageHtml, "video_id");
        }

        if (videoId is null)
        {
            LogNoVideoId(_logger, request.Url);
            return null;
        }

        var embedUrl = new Uri($"https://www.facebook.com/video/embed?video_id={videoId}");
        (string? embedHtml, _) = await TryFetchAsync(request, embedUrl, cancellationToken).ConfigureAwait(false);
        if (embedHtml is null)
        {
            return null;
        }

        if (!embedHtml.Contains("hd_src", StringComparison.Ordinal) &&
            !embedHtml.Contains("sd_src", StringComparison.Ordinal))
        {
            return null; // No playable source exposed (private/removed/login-gated) — decline gracefully.
        }

        string? mediaUrl = ExtractJsonStringField(embedHtml, "hd_src")
            ?? ExtractJsonStringField(embedHtml, "sd_src");

        if (string.IsNullOrEmpty(mediaUrl) || !Uri.TryCreate(mediaUrl, UriKind.Absolute, out Uri? resolved))
        {
            return null;
        }

        return new MediaSource
        {
            ExtractorName = Name,
            Kind = MediaKind.Progressive,
            Url = resolved,
            SuggestedFileName = $"facebook-{videoId}",
        };
    }

    private static bool LooksLikeFacebook(Uri url) =>
        url.Host.Equals("facebook.com", StringComparison.OrdinalIgnoreCase) ||
        url.Host.EndsWith(".facebook.com", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Equals("fb.watch", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetVideoId(string url)
    {
        Match match = VideoIdRegex().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Extracts a JSON string field's value (e.g. <c>"hd_src":"https:\/\/..."</c>) from raw, non-JSON page
    /// text — the embed/watch pages are HTML with embedded JSON fragments, not a single parseable
    /// document — then unescapes it via <see cref="JsonSerializer"/> so <c>\/</c>/<c>\uXXXX</c> escapes are
    /// handled correctly rather than hand-rolled.
    /// </summary>
    private static string? ExtractJsonStringField(string html, string fieldName)
    {
        try
        {
            Match match = Regex.Match(
                html,
                $"\"{Regex.Escape(fieldName)}\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
                RegexOptions.None,
                TimeSpan.FromSeconds(2));

            if (!match.Success)
            {
                return null;
            }

            return JsonSerializer.Deserialize<string>("\"" + match.Groups[1].Value + "\"");
        }
        catch (Exception ex) when (ex is RegexMatchTimeoutException or JsonException)
        {
            return null;
        }
    }

    private async Task<(string? Html, Uri? FinalUri)> TryFetchAsync(
        MediaRequest request, Uri url, CancellationToken cancellationToken)
    {
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
                return (null, null);
            }

            await using Stream stream = await response.OpenContentStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return (text, response.FinalUri);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            LogFetchException(_logger, url, ex);
            return (null, null);
        }
    }

    // v=, /reel/, or /videos/(vb.<pageid>/)?<id> — the id is always a digit run of 5+ (Facebook video ids
    // are long numeric graph ids in practice, but 5+ keeps this from matching short unrelated numbers).
    [GeneratedRegex(@"(?:[?&]v=|/reel/|/videos/(?:vb\.\d+/)?)(\d{5,})", RegexOptions.CultureInvariant)]
    private static partial Regex VideoIdRegex();

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Facebook fetch of {Url} returned status {StatusCode}; declining.")]
    private static partial void LogFetchFailed(ILogger logger, Uri url, int statusCode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Could not fetch Facebook page {Url}; declining.")]
    private static partial void LogFetchException(ILogger logger, Uri url, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Could not resolve a Facebook video id for {Url}; declining.")]
    private static partial void LogNoVideoId(ILogger logger, Uri url);
}
