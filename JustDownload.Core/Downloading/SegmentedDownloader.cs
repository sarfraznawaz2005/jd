using System.Buffers;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Storage;
using JustDownload.Core.Throttling;
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
    private readonly IClock _clock;
    private readonly IRateLimiter _globalRateLimiter;
    private readonly ILogger<SegmentedDownloader> _logger;

    public SegmentedDownloader(
        ITransport transport,
        IResourceProbe probe,
        SegmentationOptions options,
        IClock clock,
        IRateLimiter globalRateLimiter,
        ILogger<SegmentedDownloader> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(globalRateLimiter);
        ArgumentNullException.ThrowIfNull(logger);
        _transport = transport;
        _probe = probe;
        _options = options;
        _clock = clock;
        _globalRateLimiter = globalRateLimiter;
        _logger = logger;
    }

    /// <summary>
    /// Builds the limiter for a download: the shared global cap plus, when the request sets a per-download
    /// speed limit, a private bucket — both applied so the strictest wins (TASK-030).
    /// </summary>
    private IRateLimiter CreateLimiter(DownloadRequest request) =>
        request.SpeedLimit is > 0
            ? new CompositeRateLimiter(_globalRateLimiter, new TokenBucket(_clock, request.SpeedLimit.Value))
            : _globalRateLimiter;

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<long>? progress = null,
        ReceivedRanges? received = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ResourceProbeResult probe = await _probe
            .ProbeAsync(request.Url, request.Headers, cancellationToken)
            .ConfigureAwait(false);

        int requested = request.Connections ?? _options.DefaultConnections;
        int connections = probe.PlanConnectionCount(requested);
        IRateLimiter limiter = CreateLimiter(request);

        if (connections <= 1 || probe.TotalLength is not > 0)
        {
            return await DownloadSingleAsync(request, probe, limiter, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        return await DownloadSegmentedAsync(
            request, probe, probe.TotalLength.Value, connections, limiter, progress, received, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DownloadResult> DownloadSingleAsync(
        DownloadRequest request,
        ResourceProbeResult probe,
        IRateLimiter limiter,
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
        long bytes = await ThrottledCopyAsync(file, content, 0, limiter, progress, cancellationToken)
            .ConfigureAwait(false);
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
        IRateLimiter limiter,
        IProgress<long>? progress,
        ReceivedRanges? received,
        CancellationToken cancellationToken)
    {
        await using var file = PreallocatedFile.Create(request.DestinationPath, totalLength);

        // Resume: only the gaps not already on disk are fetched; a fresh download covers the whole span.
        long baseBytes = received?.TotalReceived ?? 0;
        IReadOnlyList<SegmentRange> ranges = baseBytes > 0
            ? Segmentation.SplitRanges(received!.Gaps(totalLength), connections, _options.MinSegmentSize)
            : Segmentation.Split(totalLength, connections, _options.MinSegmentSize);

        if (ranges.Count == 0)
        {
            // Everything was already received (a resume of a finished-but-uncommitted download).
            await file.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(totalLength);
            return CompletedSegmentedResult(probe, totalLength, initialSegments: 0, steals: 0);
        }

        var state = new DownloadState(ranges, baseBytes);

        Task[] workers = state.InitialSegments
            .Select(seg => Task.Run(
                () => RunWorkerAsync(
                    seg, state, file, probe.FinalUri, request.Headers, limiter, progress, received, cancellationToken),
                cancellationToken))
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
        await file.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);

        LogSegmented(_logger, state.BytesWritten, probe.FinalUri, ranges.Count, state.Steals);

        return CompletedSegmentedResult(probe, totalLength, ranges.Count, state.Steals);
    }

    private static DownloadResult CompletedSegmentedResult(
        ResourceProbeResult probe, long totalLength, int initialSegments, int steals) => new()
        {
            TotalBytes = totalLength,
            FinalUri = probe.FinalUri,
            FileName = probe.SuggestedFileName,
            SingleConnection = false,
            InitialSegments = initialSegments,
            Steals = steals,
        };

    private async Task RunWorkerAsync(
        WorkerSegment segment,
        DownloadState state,
        PreallocatedFile file,
        Uri uri,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        IRateLimiter limiter,
        IProgress<long>? progress,
        ReceivedRanges? received,
        CancellationToken cancellationToken)
    {
        // A steal must leave the victim at least one copy buffer of headroom before the split point, so a
        // read already in flight against the old end can never reach (and overlap) the stolen tail. With
        // the default 1 MiB MinStealSize this is a no-op; it only matters for tiny configured values.
        long minStealSize = Math.Max(_options.MinStealSize, PreallocatedFile.CopyBufferSize);

        WorkerSegment? current = segment;
        while (current is not null)
        {
            await DownloadSegmentAsync(current, state, file, uri, headers, limiter, progress, received, cancellationToken)
                .ConfigureAwait(false);
            current.Complete();
            current = state.TrySteal(minStealSize);
        }
    }

    private async Task DownloadSegmentAsync(
        WorkerSegment segment,
        DownloadState state,
        PreallocatedFile file,
        Uri uri,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        IRateLimiter limiter,
        IProgress<long>? progress,
        ReceivedRanges? received,
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
                // The server answered a ranged request with a full body — it can no longer resume from an
                // offset, so the already-fetched bytes are unusable and the download must restart (US-2 AC3).
                throw new ResumeNotSupportedException(
                    $"Segment {segment.Index}: server ignored the Range header (offset {offset}); resume not possible.");
            }

            long before = segment.WriteOffset;
            await using Stream content =
                await response.OpenContentStreamAsync(cancellationToken).ConfigureAwait(false);
            await PumpAsync(segment, state, file, content, limiter, progress, received, cancellationToken)
                .ConfigureAwait(false);

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
        IRateLimiter limiter,
        IProgress<long>? progress,
        ReceivedRanges? received,
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

                // Throttle to the global + per-download cap before committing the bytes (US-3).
                await limiter.AcquireAsync(writeLength, cancellationToken).ConfigureAwait(false);

                await file.WriteAsync(writeAt, buffer.AsMemory(0, writeLength), cancellationToken)
                    .ConfigureAwait(false);
                segment.Advance(writeLength);

                // Record the committed range as the resume checkpoint before reporting progress, so a pause
                // observed via progress always corresponds to bytes already captured in the checkpoint.
                received?.Add(writeAt, writeLength);
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

    /// <summary>
    /// Single-connection throttled copy: streams <paramref name="content"/> into the file from
    /// <paramref name="offset"/>, acquiring from <paramref name="limiter"/> before each write (US-3).
    /// </summary>
    private static async Task<long> ThrottledCopyAsync(
        PreallocatedFile file,
        Stream content,
        long offset,
        IRateLimiter limiter,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(PreallocatedFile.CopyBufferSize);
        try
        {
            long position = offset;
            long total = 0;
            while (true)
            {
                int read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    return total;
                }

                await limiter.AcquireAsync(read, cancellationToken).ConfigureAwait(false);
                await file.WriteAsync(position, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                position += read;
                total += read;
                progress?.Report(total);
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
