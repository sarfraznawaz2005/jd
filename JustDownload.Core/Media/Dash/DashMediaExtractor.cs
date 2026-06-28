using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Transport;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media.Dash;

/// <summary>
/// The DASH extractor (TASK-039). Recognises <c>.mpd</c> URLs (by extension or the DASH content type),
/// fetches and parses the manifest, and — when it finds downloadable progressive representations — reports
/// the media as <see cref="MediaKind.SeparateStreams"/>, surfacing the video <see cref="VideoVariant"/>s and
/// audio <see cref="AudioVariant"/>s for selection and later muxing. A manifest with no progressive
/// representation (SegmentTemplate-only) declines gracefully so the registry moves on.
/// </summary>
internal sealed partial class DashMediaExtractor : IMediaExtractor
{
    private readonly ITransport _transport;
    private readonly ILogger<DashMediaExtractor> _logger;

    public DashMediaExtractor(ITransport transport, ILogger<DashMediaExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(logger);
        _transport = transport;
        _logger = logger;
    }

    /// <summary>Runs before the generic progressive extractor (alongside HLS).</summary>
    public int Priority => 110;

    public string Name => "dash";

    public async Task<MediaSource?> TryExtractAsync(
        MediaRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!LooksLikeDash(request))
        {
            return null;
        }

        string manifestXml;
        try
        {
            manifestXml = await FetchTextAsync(request, cancellationToken).ConfigureAwait(false);
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

        DashManifest manifest;
        try
        {
            manifest = MpdParser.Parse(manifestXml, request.Url);
        }
        catch (System.Xml.XmlException ex)
        {
            LogFetchFailed(_logger, request.Url, ex);
            return null;
        }

        if (manifest.VideoRepresentations.Count == 0 && manifest.AudioRepresentations.Count == 0)
        {
            return null; // Nothing downloadable (e.g. SegmentTemplate-only) — degrade gracefully.
        }

        IReadOnlyList<VideoVariant> videoVariants = manifest.VideoRepresentations
            .Select(r => new VideoVariant(r.Uri.ToString(), r.Height ?? 0, r.Bandwidth))
            .ToArray();

        IReadOnlyList<AudioVariant> audioVariants = manifest.AudioRepresentations
            .Select(r => new AudioVariant(r.Uri.ToString(), r.Bandwidth, r.Language))
            .ToArray();

        return new MediaSource
        {
            ExtractorName = Name,
            Kind = MediaKind.SeparateStreams,
            Url = request.Url,
            SuggestedFileName = DeriveName(request.Url),
            Variants = videoVariants,
            AudioVariants = audioVariants,
        };
    }

    private static bool LooksLikeDash(MediaRequest request)
    {
        if (request.ContentType is { Length: > 0 } contentType &&
            contentType.Contains("dash+xml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string extension = Path.GetExtension(request.Url.AbsolutePath);
        return extension.Equals(".mpd", StringComparison.OrdinalIgnoreCase);
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
            throw new InvalidOperationException($"Manifest fetch returned status {response.StatusCode}.");
        }

        await using Stream stream = await response.OpenContentStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Could not fetch/parse DASH manifest {Url}; declining.")]
    private static partial void LogFetchFailed(ILogger logger, Uri url, Exception exception);
}
