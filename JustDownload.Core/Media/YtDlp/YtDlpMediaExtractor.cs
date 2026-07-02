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
/// Resolves a single best playable format via <c>yt-dlp --dump-json -f best</c> — "best" is yt-dlp's own
/// selector for a single-file format that needs no further muxing — and reports it as
/// <see cref="MediaKind.Progressive"/>, or as <see cref="MediaKind.Hls"/> when the resolved format is
/// itself an HLS media playlist (the existing HLS pipeline then downloads/decrypts/concatenates it exactly
/// as it would any other <c>.m3u8</c>). A full per-format quality picker for every yt-dlp format is out of
/// scope for this first pass; a single best-effort direct URL is enough to make sites like YouTube,
/// Facebook, and Twitter/X downloadable (see TASK-163's empirical verification notes).
/// </para>
/// <para>
/// Never throws for an expected failure mode (yt-dlp missing/unprovisioned, a non-zero exit, malformed or
/// empty JSON, no usable format in the response) — it logs and returns <see langword="null"/> so the
/// registry degrades gracefully, matching every other extractor's contract.
/// </para>
/// </summary>
internal sealed partial class YtDlpMediaExtractor : IMediaExtractor
{
    // --ignore-config: never let a stray user-level yt-dlp config (cookies-from-browser, a proxy, etc.)
    // silently change behaviour. --no-playlist: a played URL from a playlist page extracts only that
    // video. -f best: the single-file (no separate-stream muxing needed) format, per the type doc.
    private static readonly string[] BaseArguments =
        ["--dump-json", "--no-playlist", "--no-warnings", "--ignore-config", "-f", "best"];

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
            return null;
        }

        if (result.ExitCode != 0)
        {
            LogNonZeroExit(_logger, request.Url, result.ExitCode, Truncate(result.StandardError));
            return null;
        }

        return TryMap(result.StandardOutput, request.Url);
    }

    private MediaSource? TryMap(string standardOutput, Uri requestUrl)
    {
        YtDlpFormat? format;
        try
        {
            format = JsonSerializer.Deserialize<YtDlpFormat>(standardOutput, JsonOptions);
        }
        catch (JsonException ex)
        {
            LogParseFailed(_logger, requestUrl, ex);
            return null;
        }

        if (format is null)
        {
            LogNoUsableFormat(_logger, requestUrl);
            return null;
        }

        string? mediaUrlText = format.Url;
        if (string.IsNullOrEmpty(mediaUrlText) || !Uri.TryCreate(mediaUrlText, UriKind.Absolute, out Uri? mediaUrl))
        {
            LogNoUsableFormat(_logger, requestUrl);
            return null;
        }

        MediaKind kind = LooksLikeHls(format.Protocol, mediaUrlText) ? MediaKind.Hls : MediaKind.Progressive;

        return new MediaSource
        {
            ExtractorName = Name,
            Kind = kind,
            Url = mediaUrl,
            SuggestedFileName = format.Id is { Length: > 0 } id ? $"ytdlp-{id}" : null,
        };
    }

    private static bool LooksLikeHls(string? protocol, string mediaUrl) =>
        (protocol?.Contains("m3u8", StringComparison.OrdinalIgnoreCase) ?? false) ||
        mediaUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

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

/// <summary>The subset of yt-dlp's <c>--dump-json -f best</c> output this extractor consumes.</summary>
internal sealed record YtDlpFormat
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; init; }
}
