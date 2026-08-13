using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Settings;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media.YtDlp;

/// <summary>
/// The optional, user-enabled yt-dlp fallback extractor (TASK-163; D3 revised 2026-07-02 to allow yt-dlp
/// as a downloaded-on-demand, separate-process fallback — never bundled/statically linked). Runs strictly
/// last, after every in-house site-specific extractor and after <c>ProgressiveMediaExtractor</c>'s cheap
/// catch-all, because it is by far the heaviest option: a real subprocess spawn. It declines instantly —
/// no locator call, no subprocess — when the master "video capture" toggle
/// (<see cref="AppSettings.VideoCaptureEnabled"/>) is off, and declines (still no subprocess) when yt-dlp
/// is not already provisioned; provisioning is a deliberate, explicit user action (Settings' "Download
/// yt-dlp" button, TASK-162), never triggered implicitly from here.
/// <para>
/// Probes with <c>yt-dlp --dump-json</c> — deliberately without a <c>-f</c> selector (TASK-165), so yt-dlp
/// reports every format it found instead of resolving/merging one — and maps the real <c>formats</c> array
/// into <see cref="MediaSource.Variants"/> (and <see cref="MediaSource.AudioVariants"/>) so
/// <see cref="VideoQualitySelector"/> has real options, exactly as the in-house DASH/HLS extractors do. Most
/// modern sites (confirmed empirically against real YouTube formats) only expose one muxed
/// (audio+video-in-one-file) format at a low resolution and offer every higher quality as separate
/// video-only + audio-only streams, so when both exist this reports <see cref="MediaKind.SeparateStreams"/>
/// (reusing the existing separate-stream download+mux pipeline) instead of the lone low-quality muxed
/// format — otherwise the user's quality setting would have nothing meaningful to choose between. Falls
/// back to <see cref="MediaKind.Progressive"/> for muxed-only formats, or <see cref="MediaKind.Hls"/> when
/// the only usable formats are HLS media playlists. Only formats with a directly downloadable URL (plain
/// <c>http(s)</c>, or an HLS playlist) are considered; fragmented-manifest protocols this extractor's simple
/// direct-URL pipeline cannot handle (e.g. <c>http_dash_segments</c>) are skipped, as is any format entry
/// missing a usable URL or (for video streams) a resolution.
/// </para>
/// <para>
/// Declines silently (returns <see langword="null"/>) for the two gates that are not failures at all: the
/// master toggle being off, and yt-dlp not being provisioned. Every other failure mode — a non-zero exit,
/// yt-dlp failing to launch, malformed or empty JSON, no usable format in the response — means yt-dlp
/// actually looked at the URL and could not deliver, so it throws
/// <see cref="MediaExtractionFailedException"/> carrying the reason (yt-dlp's own stderr where there is
/// one). The registry records that as a failed attempt and carries on, and the UI can finally tell the user
/// "yt-dlp: Sign in to confirm you're not a bot" instead of a generic "no media found" (CLAUDE.md §5).
/// </para>
/// </summary>
internal sealed partial class YtDlpMediaExtractor : IMediaExtractor
{
    // --ignore-config: never let a stray user-level yt-dlp config (cookies-from-browser, a proxy, etc.)
    // silently change behaviour. --no-playlist: a played URL from a playlist page extracts only that video.
    // No -f selector (TASK-165): yt-dlp then just probes and reports its full "formats" array without
    // downloading or resolving/merging anything, so this extractor can map real options into Variants.
    private static readonly string[] BaseArguments =
        ["--dump-json", "--no-playlist", "--no-warnings", "--ignore-config"];

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ISettingsService _settings;
    private readonly IYtDlpLocator _locator;
    private readonly IYtDlpRunner _runner;
    private readonly ILogger<YtDlpMediaExtractor> _logger;

    public YtDlpMediaExtractor(
        ISettingsService settings, IYtDlpLocator locator, IYtDlpRunner runner, ILogger<YtDlpMediaExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _locator = locator;
        _runner = runner;
        _logger = logger;
    }

    /// <summary>Runs strictly last — after every in-house extractor, including Progressive's catch-all.</summary>
    public int Priority => int.MaxValue;

    public string Name => "yt-dlp";

    public async Task<MediaSource?> TryExtractAsync(
        MediaRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_settings.Current.VideoCaptureEnabled)
        {
            return null; // The opt-in fallback is off — decline with no locator call and no subprocess.
        }

        YtDlpInfo? ytDlp = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);
        if (ytDlp is null)
        {
            return null; // Not provisioned. Provisioning is an explicit user action, never implicit here.
        }

        string[] arguments = [.. BaseArguments, request.Url.AbsoluteUri];

        YtDlpRunResult result;
        try
        {
            result = await _runner.RunAsync(ytDlp.ExecutablePath, arguments, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is YtDlpException or IOException or InvalidOperationException)
        {
            LogRunFailed(_logger, request.Url, ex);
            throw new MediaExtractionFailedException($"couldn't run yt-dlp ({ex.Message})", ex);
        }

        if (result.ExitCode != 0)
        {
#pragma warning disable CA1873 // Truncate is a cheap length check + substring, not worth an IsEnabled guard
            LogNonZeroExit(_logger, request.Url, result.ExitCode, Truncate(result.StandardError));
#pragma warning restore CA1873
            throw new MediaExtractionFailedException(DescribeExit(result.ExitCode, result.StandardError));
        }

        return TryMap(result.StandardOutput, request.Url);
    }

    private MediaSource TryMap(string standardOutput, Uri requestUrl)
    {
        YtDlpProbeResult? probe;
        try
        {
            probe = JsonSerializer.Deserialize<YtDlpProbeResult>(standardOutput, JsonOptions);
        }
        catch (JsonException ex)
        {
            LogParseFailed(_logger, requestUrl, ex);
            throw new MediaExtractionFailedException("yt-dlp returned output this app could not read.", ex);
        }

        // Prefer the real video title (sanitized) so downloads are saved under a recognisable name; fall
        // back to the opaque id-based name only when yt-dlp reported no usable title.
        string? suggestedFileName = CrossPlatformFileName.Sanitize(probe?.Title)
            ?? (probe?.Id is { Length: > 0 } videoId ? $"ytdlp-{videoId}" : null);

        var usable = new List<(YtDlpFormat Format, Uri Url)>();
        foreach (YtDlpFormat format in probe?.Formats ?? [])
        {
            if (Uri.TryCreate(format.Url, UriKind.Absolute, out Uri? mediaUrl) &&
                IsDownloadableProtocol(format.Protocol))
            {
                usable.Add((format, mediaUrl));
            }
        }

        (YtDlpFormat Format, Uri Url)[] hls = [.. usable.Where(LooksLikeHls)];
        (YtDlpFormat Format, Uri Url)[] direct = [.. usable.Where(u => !LooksLikeHls(u))];

        (YtDlpFormat Format, Uri Url)[] muxed =
            [.. direct.Where(u => HasStream(u.Format.VideoCodec) && HasStream(u.Format.AudioCodec) && u.Format.Height is > 0)];
        (YtDlpFormat Format, Uri Url)[] videoOnly =
            [.. direct.Where(u => HasStream(u.Format.VideoCodec) && !HasStream(u.Format.AudioCodec) && u.Format.Height is > 0)];
        (YtDlpFormat Format, Uri Url)[] audioOnly =
            [.. direct.Where(u => !HasStream(u.Format.VideoCodec) && HasStream(u.Format.AudioCodec))];

        // Prefer separate video-only + audio-only streams over a lone low-quality muxed format (confirmed
        // empirically: sites like YouTube only muxed one low resolution; every higher quality is separate).
        if (videoOnly.Length > 0 && audioOnly.Length > 0)
        {
            return new MediaSource
            {
                ExtractorName = Name,
                Kind = MediaKind.SeparateStreams,
                Url = requestUrl,
                SuggestedFileName = suggestedFileName,
                Variants = [.. videoOnly.Select(u => ToVideoVariant(u.Format, u.Url))],
                AudioVariants = [.. audioOnly.Select(u => ToAudioVariant(u.Format, u.Url))],
            };
        }

        if (muxed.Length > 0)
        {
            return new MediaSource
            {
                ExtractorName = Name,
                Kind = MediaKind.Progressive,
                Url = requestUrl,
                SuggestedFileName = suggestedFileName,
                Variants = [.. muxed.Select(u => ToVideoVariant(u.Format, u.Url))],
            };
        }

        if (hls.Length > 0)
        {
            return new MediaSource
            {
                ExtractorName = Name,
                Kind = MediaKind.Hls,
                Url = requestUrl,
                SuggestedFileName = suggestedFileName,
                Variants = [.. hls.Select(u => ToVideoVariant(u.Format, u.Url))],
            };
        }

        LogNoUsableFormat(_logger, requestUrl);
        throw new MediaExtractionFailedException(
            "yt-dlp found no downloadable format this app can handle at that URL.");
    }

    /// <summary>
    /// yt-dlp writes the real cause as the last "ERROR: …" line of stderr (bot challenge, HTTP 429, missing
    /// JS runtime, unsupported URL). Prefer that over the exit code, which says nothing on its own.
    /// </summary>
    private static string DescribeExit(int exitCode, string standardError)
    {
        string? line = standardError
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.Length > 0);

        if (line is null)
        {
            return string.Create(CultureInfo.InvariantCulture, $"yt-dlp exited with code {exitCode}.");
        }

        return line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)
            ? line["ERROR:".Length..].Trim()
            : line;
    }

    private static VideoVariant ToVideoVariant(YtDlpFormat format, Uri url) =>
        new(
            url.ToString(),
            format.Height ?? 0,
            ToBitsPerSecond(format.TotalBitrateKbps ?? format.VideoBitrateKbps),
            format.Fps,
            ToFriendlyCodec(format.VideoCodec));

    /// <summary>
    /// Maps yt-dlp's raw <c>vcodec</c> string (e.g. <c>avc1.42001E</c>, <c>vp9</c>, <c>av01.0.05M.08</c>) to
    /// a short, human-friendly label for the quality picker (TASK-166). Falls back to the raw string for a
    /// codec this doesn't recognise, and to <see langword="null"/> when there is no video stream.
    /// </summary>
    private static string? ToFriendlyCodec(string? vcodec)
    {
        if (!HasStream(vcodec))
        {
            return null;
        }

        if (vcodec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase) ||
            vcodec.StartsWith("h264", StringComparison.OrdinalIgnoreCase))
        {
            return "H.264";
        }

        if (vcodec.StartsWith("vp9", StringComparison.OrdinalIgnoreCase))
        {
            return "VP9";
        }

        if (vcodec.StartsWith("av01", StringComparison.OrdinalIgnoreCase))
        {
            return "AV1";
        }

        return vcodec;
    }

    private static AudioVariant ToAudioVariant(YtDlpFormat format, Uri url) =>
        new(url.ToString(), ToBitsPerSecond(format.TotalBitrateKbps ?? format.AudioBitrateKbps));

    private static long? ToBitsPerSecond(double? kilobitsPerSecond) =>
        kilobitsPerSecond is > 0 ? (long)(kilobitsPerSecond.Value * 1000) : null;

    private static bool HasStream([NotNullWhen(true)] string? codec) =>
        !string.IsNullOrEmpty(codec) && !codec.Equals("none", StringComparison.OrdinalIgnoreCase);

    // Only protocols this extractor's simple "GET the URL" pipeline can actually handle: a plain
    // progressively-fetchable http(s) URL, or an HLS playlist (downloaded/decrypted/concatenated by the
    // existing HLS pipeline). Anything else — e.g. "http_dash_segments" fragmented delivery — needs manifest
    // expansion this extractor doesn't do, so it's skipped rather than fed in as a bogus direct URL.
    private static bool IsDownloadableProtocol(string? protocol) =>
        protocol is { Length: > 0 } &&
        (protocol.Equals("http", StringComparison.OrdinalIgnoreCase) ||
            protocol.Equals("https", StringComparison.OrdinalIgnoreCase) ||
            protocol.Contains("m3u8", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeHls((YtDlpFormat Format, Uri Url) entry) =>
        (entry.Format.Protocol?.Contains("m3u8", StringComparison.OrdinalIgnoreCase) ?? false) ||
        entry.Url.AbsoluteUri.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value) => value.Length > 500 ? value[..500] : value;

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "yt-dlp failed to run for {Url}; declining.")]
    private static partial void LogRunFailed(ILogger logger, Uri url, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "yt-dlp exited {ExitCode} for {Url}: {Error}")]
    private static partial void LogNonZeroExit(ILogger logger, Uri url, int exitCode, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Could not parse yt-dlp JSON output for {Url}; declining.")]
    private static partial void LogParseFailed(ILogger logger, Uri url, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "yt-dlp reported no usable format for {Url}; declining.")]
    private static partial void LogNoUsableFormat(ILogger logger, Uri url);
}

/// <summary>The subset of yt-dlp's <c>--dump-json</c> output this extractor consumes: the video id (for the
/// suggested file name) and the real <c>formats</c> array (TASK-165).</summary>
internal sealed record YtDlpProbeResult
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>The video's real title, used (sanitized) as the suggested download file name.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("formats")]
    public IReadOnlyList<YtDlpFormat>? Formats { get; init; }
}

/// <summary>One entry of yt-dlp's <c>formats</c> array — a single downloadable rendition of the video.</summary>
internal sealed record YtDlpFormat
{
    [JsonPropertyName("format_id")]
    public string? FormatId { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; init; }

    /// <summary>Vertical resolution in pixels; <see langword="null"/> for audio-only formats.</summary>
    [JsonPropertyName("height")]
    public int? Height { get; init; }

    /// <summary>The video codec, or the literal string <c>"none"</c> when this format carries no video.</summary>
    [JsonPropertyName("vcodec")]
    public string? VideoCodec { get; init; }

    /// <summary>The audio codec, or the literal string <c>"none"</c> when this format carries no audio.</summary>
    [JsonPropertyName("acodec")]
    public string? AudioCodec { get; init; }

    /// <summary>Total average bitrate in Kbit/s, when yt-dlp reports one for this format.</summary>
    [JsonPropertyName("tbr")]
    public double? TotalBitrateKbps { get; init; }

    /// <summary>Video-only average bitrate in Kbit/s, when yt-dlp reports one for this format.</summary>
    [JsonPropertyName("vbr")]
    public double? VideoBitrateKbps { get; init; }

    /// <summary>Audio-only average bitrate in Kbit/s, when yt-dlp reports one for this format.</summary>
    [JsonPropertyName("abr")]
    public double? AudioBitrateKbps { get; init; }

    /// <summary>Frame rate, when yt-dlp reports one for this format (video formats only).</summary>
    [JsonPropertyName("fps")]
    public double? Fps { get; init; }
}
