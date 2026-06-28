using System.Buffers;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Storage;
using JustDownload.Core.Throttling;
using JustDownload.Core.Transport;
using JustDownload.Core.Transport.Proxy;
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
    private readonly IProxyService _proxy;
    private readonly ILogger<SegmentedDownloader> _logger;

    public SegmentedDownloader(
        ITransport transport,
        IResourceProbe probe,
        SegmentationOptions options,
        IClock clock,
        IRateLimiter globalRateLimiter,
        IProxyService proxy,
        ILogger<SegmentedDownloader> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(globalRateLimiter);
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(logger);
        _transport = transport;
        _probe = probe;
        _options = options;
        _clock = clock;
        _globalRateLimiter = globalRateLimiter;
        _proxy = proxy;
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

    // A 403/410 mid-download conventionally means a time-limited/signed link has lapsed; surface it as an
    // expiry so the manager can offer a renew rather than reporting a generic failure (TASK-032, US-13).
    private static void ThrowIfExpired(int statusCode)
    {
        if (statusCode is 403 or 410)
        {
            throw new DownloadExpiredException($"The download link has expired (server returned {statusCode}).")
            {
                StatusCode = statusCode,
            };
        }
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<long>? progress = null,
        ReceivedRanges? received = null,
        IProgress<ConnectionProgress>? connectionProgress = null,
        ConnectionController? connections = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Install this download's proxy override for the whole operation; it flows (via AsyncLocal) to the
        // probe and every segment worker, then is cleared on completion. A null override uses the global proxy.
        using IDisposable proxyScope = _proxy.BeginDownloadScope(request.Proxy);

        ResourceProbeResult probe = await _probe
            .ProbeAsync(request.Url, request.Headers, cancellationToken)
            .ConfigureAwait(false);

        int requested = request.Connections ?? _options.DefaultConnections;
        int connectionCount = probe.PlanConnectionCount(requested);
        IRateLimiter limiter = CreateLimiter(request);

        if (connectionCount <= 1 || probe.TotalLength is not > 0)
        {
            return await DownloadSingleAsync(
                request, probe, limiter, progress, connectionProgress, connections, cancellationToken)
                .ConfigureAwait(false);
        }

        return await DownloadSegmentedAsync(
            request, probe, probe.TotalLength.Value, connectionCount, limiter, progress, received,
            connectionProgress, connections, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DownloadResult> DownloadSingleAsync(
        DownloadRequest request,
        ResourceProbeResult probe,
        IRateLimiter limiter,
        IProgress<long>? progress,
        IProgress<ConnectionProgress>? connectionProgress,
        ConnectionController? connections,
        CancellationToken cancellationToken)
    {
        // A single-connection transfer cannot be parallelised; live connection control is a no-op here
        // beyond reflecting the one active connection (AC2).
        connections?.ReportActiveConnections(1);
        long totalLength = probe.TotalLength is > 0 ? probe.TotalLength.Value : 0;
        await using var file = PreallocatedFile.Create(request.DestinationPath, totalLength);

        await using ITransportResponse response = await _transport
            .SendAsync(new TransportRequest { Uri = probe.FinalUri, Headers = request.Headers }, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            ThrowIfExpired(response.StatusCode);
            throw new IOException($"Download failed: server returned status {response.StatusCode}.");
        }

        await using Stream content = await response.OpenContentStreamAsync(cancellationToken).ConfigureAwait(false);
        long bytes = await ThrottledCopyAsync(
            file, content, 0, totalLength, limiter, progress, connectionProgress, cancellationToken)
            .ConfigureAwait(false);
        await file.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
        connectionProgress?.Report(new ConnectionProgress
        {
            ConnectionId = 0,
            SegmentIndex = 0,
            Start = 0,
            End = Math.Max(0, bytes - 1),
            Position = bytes,
            IsComplete = true,
        });

        connections?.ReportActiveConnections(0);
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
        int connectionCount,
        IRateLimiter limiter,
        IProgress<long>? progress,
        ReceivedRanges? received,
        IProgress<ConnectionProgress>? connectionProgress,
        ConnectionController? connections,
        CancellationToken cancellationToken)
    {
        await using var file = PreallocatedFile.Create(request.DestinationPath, totalLength);

        // Resume: only the gaps not already on disk are fetched; a fresh download covers the whole span.
        long baseBytes = received?.TotalReceived ?? 0;
        IReadOnlyList<SegmentRange> ranges = baseBytes > 0
            ? Segmentation.SplitRanges(received!.Gaps(totalLength), connectionCount, _options.MinSegmentSize)
            : Segmentation.Split(totalLength, connectionCount, _options.MinSegmentSize);

        if (ranges.Count == 0)
        {
            // Everything was already received (a resume of a finished-but-uncommitted download).
            await file.FlushToDiskAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(totalLength);
            return CompletedSegmentedResult(probe, totalLength, initialSegments: 0, steals: 0);
        }

        var state = new DownloadState(ranges, baseBytes);
        var pool = new WorkerPool(
            this, state, file, probe.FinalUri, request.Headers, limiter, progress, received, connectionProgress,
            connections, connectionCount, Math.Max(_options.MinStealSize, PreallocatedFile.CopyBufferSize),
            cancellationToken);

        await pool.RunAsync().ConfigureAwait(false);
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

    /// <summary>
    /// The dynamic pool of download workers for one segmented transfer (TASK-027). It spawns one worker per
    /// initial segment, then honours a live <see cref="ConnectionController"/>: <see cref="Reconcile"/>
    /// spawns extra workers (each starting on a stolen tail) when the desired count rises, and each worker
    /// retires at a segment boundary when the desired count has dropped below the active count — a clean
    /// drain that never abandons in-flight bytes. The active count is written back to the controller. When
    /// no controller is supplied the count is fixed at the planned connection count and behaviour matches
    /// the original static pool. A worker fault cancels its siblings and propagates.
    /// </summary>
    private sealed class WorkerPool
    {
        private readonly object _gate = new();
        private readonly List<Task> _workers = [];
        private readonly CancellationTokenSource _failureCts;
        private readonly SegmentedDownloader _owner;
        private readonly DownloadState _state;
        private readonly PreallocatedFile _file;
        private readonly Uri _uri;
        private readonly IReadOnlyList<KeyValuePair<string, string>> _headers;
        private readonly IRateLimiter _limiter;
        private readonly IProgress<long>? _progress;
        private readonly ReceivedRanges? _received;
        private readonly IProgress<ConnectionProgress>? _connectionProgress;
        private readonly ConnectionController? _controller;
        private readonly int _baseDesired;
        private readonly long _minStealSize;
        private int _active;
        private int _nextConnectionId;

        public WorkerPool(
            SegmentedDownloader owner,
            DownloadState state,
            PreallocatedFile file,
            Uri uri,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            IRateLimiter limiter,
            IProgress<long>? progress,
            ReceivedRanges? received,
            IProgress<ConnectionProgress>? connectionProgress,
            ConnectionController? controller,
            int baseDesired,
            long minStealSize,
            CancellationToken cancellationToken)
        {
            _owner = owner;
            _state = state;
            _file = file;
            _uri = uri;
            _headers = headers;
            _limiter = limiter;
            _progress = progress;
            _received = received;
            _connectionProgress = connectionProgress;
            _controller = controller;
            _baseDesired = baseDesired;
            _minStealSize = minStealSize;
            _failureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        private int Desired => _controller?.DesiredConnections ?? _baseDesired;

        public async Task RunAsync()
        {
            if (_controller is not null)
            {
                _controller.DesiredChanged += OnDesiredChanged;
            }

            try
            {
                lock (_gate)
                {
                    // Every initial segment must get a worker — leaving one unworked would let it be stolen
                    // (truncated) yet never downloaded. A lower desired count is honoured by draining at
                    // boundaries, not by skipping initial segments.
                    foreach (WorkerSegment seg in _state.InitialSegments)
                    {
                        _active++;
                        SpawnLocked(seg);
                    }

                    // Reported under the lock so concurrent count changes are observed in a consistent order.
                    _controller?.ReportActiveConnections(_active);
                }

                // If the desired count already exceeds the initial split, spawn stealers up front.
                Reconcile();

                while (true)
                {
                    Task[] snapshot;
                    lock (_gate)
                    {
                        snapshot = _workers.ToArray();
                    }

                    if (snapshot.Length == 0)
                    {
                        break;
                    }

                    await Task.WhenAll(snapshot).ConfigureAwait(false);

                    lock (_gate)
                    {
                        _workers.RemoveAll(t => t.IsCompleted);
                        if (_workers.Count == 0)
                        {
                            break;
                        }
                    }
                }
            }
            finally
            {
                if (_controller is not null)
                {
                    _controller.DesiredChanged -= OnDesiredChanged;
                }

                _failureCts.Dispose();
            }
        }

        private void OnDesiredChanged() => Reconcile();

        private void SpawnLocked(WorkerSegment seg)
        {
            int connectionId = _nextConnectionId++;
            _workers.Add(Task.Run(() => WorkerLoopAsync(seg, connectionId), _failureCts.Token));
        }

        private void Reconcile()
        {
            lock (_gate)
            {
                if (_failureCts.IsCancellationRequested)
                {
                    return;
                }

                bool changed = false;
                while (_active < Desired)
                {
                    WorkerSegment? seg = _state.TrySteal(_minStealSize);
                    if (seg is null)
                    {
                        break; // No splittable work to hand a new connection.
                    }

                    _active++;
                    changed = true;
                    SpawnLocked(seg);
                }

                if (changed)
                {
                    _controller?.ReportActiveConnections(_active);
                }
            }
        }

        private WorkerSegment? NextAssignment()
        {
            lock (_gate)
            {
                if (_active > Desired)
                {
                    // The desired count dropped — this connection drains cleanly at the boundary.
                    _active--;
                    _controller?.ReportActiveConnections(_active);
                    return null;
                }

                WorkerSegment? seg = _state.TrySteal(_minStealSize);
                if (seg is null)
                {
                    _active--;
                    _controller?.ReportActiveConnections(_active);
                    return null;
                }

                return seg; // Keep this connection alive on the stolen tail.
            }
        }

        private async Task WorkerLoopAsync(WorkerSegment segment, int connectionId)
        {
            WorkerSegment last = segment;
            WorkerSegment? current = segment;
            try
            {
                while (current is not null)
                {
                    last = current;
                    await _owner.DownloadSegmentAsync(
                        current, connectionId, _state, _file, _uri, _headers, _limiter, _progress, _received,
                        _connectionProgress, _failureCts.Token).ConfigureAwait(false);
                    current.Complete();
                    current = NextAssignment();
                }

                // This connection has no more work — report it idle so the UI can retire its live row.
                _connectionProgress?.Report(new ConnectionProgress
                {
                    ConnectionId = connectionId,
                    SegmentIndex = last.Index,
                    Start = last.Start,
                    End = last.EndInclusive,
                    Position = last.WriteOffset,
                    IsComplete = true,
                });
            }
            catch
            {
                lock (_gate)
                {
                    _active = Math.Max(0, _active - 1);
                    _controller?.ReportActiveConnections(_active);
                }

                await _failureCts.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task DownloadSegmentAsync(
        WorkerSegment segment,
        int connectionId,
        DownloadState state,
        PreallocatedFile file,
        Uri uri,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        IRateLimiter limiter,
        IProgress<long>? progress,
        ReceivedRanges? received,
        IProgress<ConnectionProgress>? connectionProgress,
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
                ThrowIfExpired(response.StatusCode);
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
            await PumpAsync(
                segment, connectionId, state, file, content, limiter, progress, received, connectionProgress,
                cancellationToken).ConfigureAwait(false);

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
        int connectionId,
        DownloadState state,
        PreallocatedFile file,
        Stream content,
        IRateLimiter limiter,
        IProgress<long>? progress,
        ReceivedRanges? received,
        IProgress<ConnectionProgress>? connectionProgress,
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
                connectionProgress?.Report(new ConnectionProgress
                {
                    ConnectionId = connectionId,
                    SegmentIndex = segment.Index,
                    Start = segment.Start,
                    End = segment.EndInclusive,
                    Position = segment.WriteOffset,
                });

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
        long knownTotal,
        IRateLimiter limiter,
        IProgress<long>? progress,
        IProgress<ConnectionProgress>? connectionProgress,
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
                connectionProgress?.Report(new ConnectionProgress
                {
                    ConnectionId = 0,
                    SegmentIndex = 0,
                    Start = offset,
                    End = knownTotal > 0 ? knownTotal - 1 : position - 1,
                    Position = position,
                });
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
