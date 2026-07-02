using System.Globalization;
using JustDownload.Core.Transport;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media.Dash;

/// <summary>
/// Default <see cref="IDashSegmentDownloader"/> (TASK-102). Splits the identifier URI back into the manifest
/// location and representation id, re-fetches and re-parses the manifest, resolves the representation's
/// ordered segment URIs (init segment first, if any), and downloads them with bounded parallelism over the
/// shared <see cref="ITransport"/> — the same shape as <see cref="Hls.IHlsDownloader"/>. Segments are written
/// in order as <c>seg00000.bin</c>, <c>seg00001.bin</c>, … ready for concatenation. Cancellation is honoured
/// promptly.
/// </summary>
internal sealed partial class DashSegmentDownloader : IDashSegmentDownloader
{
    private readonly ITransport _transport;
    private readonly DashOptions _options;
    private readonly ILogger<DashSegmentDownloader> _logger;

    public DashSegmentDownloader(ITransport transport, DashOptions options, ILogger<DashSegmentDownloader> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _transport = transport;
        _options = options;
        _logger = logger;
    }

    public async Task<DashSegmentDownloadResult> DownloadAsync(
        Uri representationUri,
        string workingDirectory,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null,
        IProgress<DashSegmentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(representationUri);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        headers ??= [];
        Directory.CreateDirectory(workingDirectory);

        if (!MpdParser.TryParseRepresentationUri(representationUri, out Uri? manifestUri, out string? representationId))
        {
            throw new DashExtractionException($"'{representationUri}' is not a resolvable DASH representation URI.");
        }

        string manifestXml = await FetchTextAsync(manifestUri, headers, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<Uri>? segments;
        try
        {
            segments = MpdParser.ResolveSegments(manifestXml, manifestUri, representationId);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new DashExtractionException($"Could not parse the DASH manifest at '{manifestUri}'.", ex);
        }

        if (segments is null || segments.Count == 0)
        {
            throw new DashExtractionException(
                $"Representation '{representationId}' in '{manifestUri}' has no resolvable segments.");
        }

        var segmentFiles = new string[segments.Count];
        int totalSegments = segments.Count;
        int completed = 0;
        long downloadedBytes = 0;

        using var throttle = new SemaphoreSlim(Math.Max(1, _options.MaxParallelSegments));
        var tasks = new List<Task>(totalSegments);

        for (int index = 0; index < segments.Count; index++)
        {
            int segmentIndex = index;
            Uri segmentUri = segments[segmentIndex];

            tasks.Add(Task.Run(
                async () =>
                {
                    await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        byte[] data = await FetchBytesAsync(segmentUri, headers, cancellationToken).ConfigureAwait(false);

                        string path = Path.Combine(
                            workingDirectory,
                            string.Create(CultureInfo.InvariantCulture, $"seg{segmentIndex:D5}.bin"));
                        await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
                        segmentFiles[segmentIndex] = path;

                        int done = Interlocked.Increment(ref completed);
                        long bytes = Interlocked.Add(ref downloadedBytes, data.Length);
                        progress?.Report(new DashSegmentProgress(done, totalSegments, bytes));
                    }
                    finally
                    {
                        throttle.Release();
                    }
                },
                cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        LogDownloaded(_logger, totalSegments, manifestUri);
        return new DashSegmentDownloadResult(segmentFiles, Interlocked.Read(ref downloadedBytes));
    }

    private async Task<byte[]> FetchBytesAsync(
        Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
    {
        var request = new TransportRequest { Uri = uri, Method = TransportMethod.Get, Headers = headers };
        await using ITransportResponse response = await _transport.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new DashExtractionException($"Fetching '{uri}' failed with status {response.StatusCode}.");
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
        catch (DashExtractionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DashExtractionException($"Could not fetch the DASH manifest at '{uri}'.", ex);
        }

        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Downloaded {Count} DASH segments from {Url}.")]
    private static partial void LogDownloaded(ILogger logger, int count, Uri url);
}
