using System.Buffers;
using JustDownload.Core.Storage;
using JustDownload.Core.Transport;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Downloading;

/// <summary>
/// Default <see cref="ISegmentedDownloader"/> (TASK-026). Probes the resource, then either streams it on
/// one connection (range-less/unknown size) or splits it into segments that download concurrently into a
/// pre-allocated sparse file. When a connection finishes, it re-splits (steals) the tail of the largest
/// remaining segment so no slow segment stalls completion. Live truncation is coordinated by lowering a
/// victim segment's end under a lock; each copy loop clamps its writes to the current end, so a steal
/// never causes overlapping or lost bytes.
/// </summary>
internal sealed partial class SegmentedDownloader : ISegmentedDownloader
{
    private readonly ITransport _transport;
    private readonly IResourceProbe _probe;
    private readonly SegmentationOptions _options;
    private readonly ILogger<SegmentedDownloader> _logger;

    public SegmentedDownloader(
        ITransport transport,
        IResourceProbe probe,
        SegmentationOptions options,
        ILogger<SegmentedDownloader> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _transport = transport;
        _probe = probe;
        _options = options;
        _logger = logger;
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ResourceProbeResult probe = await _probe
            .ProbeAsync(request.Url, request.Headers, cancellationToken)
            .ConfigureAwait(false);

        int requested = request.Connections ?? _options.DefaultConnections;
        int connections = probe.PlanConnectionCount(requested);

        if (connections <= 1 || probe.TotalLength is not > 0)
        {
            return await DownloadSingleAsync(request, probe, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        return await DownloadSegmentedAsync(
            request, probe, probe.TotalLength.Value, connections, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DownloadResult> DownloadSingleAsync(
        DownloadRequest request,
        ResourceProbeResult probe,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        await using var file = PreallocatedFile.Create(
            request.DestinationPath, probe.TotalLength is > 0 ? probe.TotalLength.Value : 0);

        await using ITransportResponse response = await _transport
            .SendAsync(new TransportRequest { Uri = probe.FinalUri, Headers = request.Headers }, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new IOException($"Download failed: server returned status {response.StatusCode}.");
        }

        await using Stream content = await response.OpenContentStreamAsync(cancellationToken).ConfigureAwait(false);
        long bytes = await file.CopyFromAsync(content, 0, progress, cancellationToken).ConfigureAwait(false);
        await file.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);

        LogSingleConnection(_logger, bytes, probe.FinalUri);

        return new DownloadResult
        {
            TotalBytes = bytes,
            FinalUri = probe.FinalUri,
            FileName = probe.SuggestedFileName,
            SingleConnection = true,
            InitialSegments = 1,
            Steals = 0,
        };
    }

    private async Task<DownloadResult> DownloadSegmentedAsync(
        DownloadRequest request,
        ResourceProbeResult probe,
        long totalLength,
        int connections,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SegmentRange> ranges =
            Segmentation.Split(totalLength, connections, _options.MinSegmentSize);

        await using var file = PreallocatedFile.Create(request.DestinationPath, totalLength);

        var state = new DownloadState(ranges);

        Task[] workers = state.InitialSegments
            .Select(seg => Task.Run(
                () => RunWorkerAsync(seg, state, file, probe.FinalUri, request.Headers, progress, cancellationToken),
                cancellationToken))
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
        await file.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);

        LogSegmented(_logger, state.BytesWritten, probe.FinalUri, ranges.Count, state.Steals);

        return new DownloadResult
        {
            TotalBytes = totalLength,
            FinalUri = probe.FinalUri,
            FileName = probe.SuggestedFileName,
            SingleConnection = false,
            InitialSegments = ranges.Count,
            Steals = state.Steals,
        };
    }

    private async Task RunWorkerAsync(
        WorkerSegment segment,
        DownloadState state,
        PreallocatedFile file,
        Uri uri,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        WorkerSegment? current = segment;
        while (current is not null)
        {
            await DownloadSegmentAsync(current, state, file, uri, headers, progress, cancellationToken)
                .ConfigureAwait(false);
            current.Complete();
            current = state.TrySteal(_options.MinStealSize);
        }
    }

    private async Task DownloadSegmentAsync(
        WorkerSegment segment,
        DownloadState state,
        PreallocatedFile file,
        Uri uri,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        int stallGuard = 0;
        while (true)
        {
            long offset = segment.WriteOffset;
            long end = segment.EndInclusive;
            if (offset > end)
            {
                return;
            }

            var requestMessage = new TransportRequest
            {
                Uri = uri,
                Headers = headers,
                Range = new ByteRange(offset, end),
            };

            await using ITransportResponse response =
                await _transport.SendAsync(requestMessage, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new IOException(
                    $"Segment {segment.Index} failed: server returned status {response.StatusCode}.");
            }

            if (offset > 0 && !response.IsPartialContent)
            {
                throw new InvalidOperationException(
                    $"Segment {segment.Index}: server ignored the Range header mid-download.");
            }

            long before = segment.WriteOffset;
            await using Stream content =
                await response.OpenContentStreamAsync(cancellationToken).ConfigureAwait(false);
            await PumpAsync(segment, state, file, content, progress, cancellationToken).ConfigureAwait(false);

            if (segment.WriteOffset > segment.EndInclusive)
            {
                return; // Finished this segment (or it was truncated to nothing by a steal).
            }

            // The stream ended before the range was complete (e.g. a dropped connection): re-request the
            // remaining bytes. Guard against a server that keeps closing without progress.
            if (segment.WriteOffset == before)
            {
                if (++stallGuard >= 3)
                {
                    throw new IOException($"Segment {segment.Index} stalled at offset {segment.WriteOffset}.");
                }
            }
            else
            {
                stallGuard = 0;
            }
        }
    }

    private static async Task PumpAsync(
        WorkerSegment segment,
        DownloadState state,
        PreallocatedFile file,
        Stream content,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(PreallocatedFile.CopyBufferSize);
        try
        {
            while (true)
            {
                long writeAt = segment.WriteOffset;
                long allowed = segment.EndInclusive - writeAt + 1;
                if (allowed <= 0)
                {
                    return; // Reached the end, or a steal truncated this segment.
                }

                int toRead = (int)Math.Min(buffer.Length, allowed);
                int read = await content.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    return; // Stream ended; the caller decides whether to re-request.
                }

                // A steal may have lowered the end mid-read; clamp the write so this segment never writes
                // past its (possibly new) end. Any read-but-not-written bytes are simply dropped — the
                // stealing worker re-fetches that tail — so there is no overlap or corruption.
                long writable = segment.EndInclusive - writeAt + 1;
                if (writable <= 0)
                {
                    return;
                }

                int writeLength = (int)Math.Min(read, writable);
                await file.WriteAsync(writeAt, buffer.AsMemory(0, writeLength), cancellationToken)
                    .ConfigureAwait(false);
                segment.Advance(writeLength);
                progress?.Report(state.AddBytes(writeLength));

                if (writeLength < read)
                {
                    return; // Hit the end (or a fresh steal boundary); stop this assignment.
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Downloaded {Bytes} bytes from {Uri} on a single connection.")]
    private static partial void LogSingleConnection(ILogger logger, long bytes, Uri uri);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Downloaded {Bytes} bytes from {Uri} across {Segments} segments ({Steals} steals).")]
    private static partial void LogSegmented(ILogger logger, long bytes, Uri uri, int segments, int steals);
}
