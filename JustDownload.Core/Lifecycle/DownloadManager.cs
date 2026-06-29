using System.Collections.Concurrent;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Downloading;
using JustDownload.Core.Logging;
using JustDownload.Core.Security;
using JustDownload.Core.Transport;
using JustDownload.Core.Transport.Auth;
using JustDownload.Core.Transport.Proxy;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Lifecycle;

/// <summary>
/// Default <see cref="IDownloadManager"/> (TASK-031). Persists the lifecycle of each download through the
/// <see cref="IDownloadRepository"/>, drives transfers through the <see cref="ISegmentedDownloader"/>, and
/// turns the downloader's raw byte-count progress into <see cref="DownloadProgress"/> snapshots (speed via a
/// <see cref="SpeedEstimator"/>, ETA derived from the total). Every state change is validated against
/// <see cref="DownloadStateMachine"/>, persisted, and surfaced through <see cref="StatusChanged"/> /
/// <see cref="ProgressChanged"/> so the UI never has to poll.
/// </summary>
internal sealed partial class DownloadManager : IDownloadManager
{
    /// <summary>How often the resume checkpoint is flushed to the database during an active download.</summary>
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound on UI progress notifications per download (~15Hz) — coalesces per-chunk reports.</summary>
    private static readonly TimeSpan ProgressEmitInterval = TimeSpan.FromMilliseconds(66);

    private readonly IDownloadRepository _repository;
    private readonly ISegmentRepository _segments;
    private readonly ISegmentedDownloader _downloader;
    private readonly IResourceProbe _probe;
    private readonly SegmentationOptions _segmentationOptions;
    private readonly ISecretStore _secretStore;
    private readonly IRetryBackoff _backoff;
    private readonly IProxyService _proxy;
    private readonly IClock _clock;
    private readonly ILogger<DownloadManager> _logger;
    private readonly ConcurrentDictionary<long, DownloadProgress> _latest = new();
    private readonly ConcurrentDictionary<long, ConnectionTracker> _connections = new();
    private readonly ProgressEmitThrottle _progressThrottle = new(ProgressEmitInterval);

    public DownloadManager(
        IDownloadRepository repository,
        ISegmentRepository segments,
        ISegmentedDownloader downloader,
        IResourceProbe probe,
        SegmentationOptions segmentationOptions,
        ISecretStore secretStore,
        IRetryBackoff backoff,
        IProxyService proxy,
        IClock clock,
        ILogger<DownloadManager> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(segmentationOptions);
        ArgumentNullException.ThrowIfNull(secretStore);
        ArgumentNullException.ThrowIfNull(backoff);
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _segments = segments;
        _downloader = downloader;
        _probe = probe;
        _segmentationOptions = segmentationOptions;
        _secretStore = secretStore;
        _backoff = backoff;
        _proxy = proxy;
        _clock = clock;
        _logger = logger;
    }

    public event EventHandler<DownloadStatusChangedEventArgs>? StatusChanged;

    public event EventHandler<DownloadProgressChangedEventArgs>? ProgressChanged;

    public DownloadProgress? GetProgress(long id) => _latest.GetValueOrDefault(id);

    public IReadOnlyList<ConnectionStat> GetConnections(long id) =>
        _connections.TryGetValue(id, out ConnectionTracker? tracker) ? tracker.Snapshot() : [];

    public async Task<long> EnqueueAsync(
        EnqueueDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.DestinationDirectory);
        ArgumentException.ThrowIfNullOrEmpty(request.FileName);

        // Cookies are secrets (§5): keep them in the OS keychain and persist only the opaque reference.
        string? cookieSecretRef = null;
        if (request.Cookies is { Length: > 0 } cookies)
        {
            cookieSecretRef = await _secretStore.StoreAsync(cookies, cancellationToken).ConfigureAwait(false);
        }

        // A per-download proxy override (TASK-153): persist the config columns; the password (if any) goes to
        // the keychain like every other secret and only its reference is stored.
        ProxyOverridePersistence proxy = await PersistProxyOverrideAsync(request.Proxy, cancellationToken)
            .ConfigureAwait(false);

        var record = new Download
        {
            Url = request.Url.ToString(),
            Referrer = request.Referrer,
            Filename = request.FileName,
            Directory = request.DestinationDirectory,
            TotalBytes = request.TotalBytes,
            Status = DownloadStatusCodes.Queued,
            CategoryType = request.CategoryType,
            CategoryStatus = "Incomplete",
            CreatedAt = _clock.UtcNow,
            MaxConnections = request.MaxConnections,
            SpeedLimit = request.SpeedLimit,
            CookieSecretRef = cookieSecretRef,
            ProxyKind = proxy.Kind,
            ProxyHost = proxy.Host,
            ProxyPort = proxy.Port,
            ProxyUsername = proxy.Username,
            ProxyDomain = proxy.Domain,
            ProxyPasswordSecretRef = proxy.PasswordSecretRef,
        };

        long id = await _repository.AddAsync(record, cancellationToken).ConfigureAwait(false);
        LogEnqueued(_logger, id, SafeLogUrl.Of(record.Url));
        RaiseStatus(id, previous: null, DownloadStatus.Queued);
        return id;
    }

    public async Task<DownloadResult> StartAsync(long id, CancellationToken cancellationToken = default)
    {
        Download record = await _repository.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No download exists with id {id}.");

        DownloadStatus from = DownloadStatusCodes.Parse(record.Status);
        DownloadStateMachine.EnsureCanTransition(from, DownloadStatus.Active);

        if (string.IsNullOrEmpty(record.Directory) || string.IsNullOrEmpty(record.Filename))
        {
            throw new InvalidOperationException(
                $"Download {id} has no destination path resolved and cannot be started.");
        }

        Download active = record with { Status = DownloadStatusCodes.Active, Error = null };
        await _repository.UpdateAsync(active, cancellationToken).ConfigureAwait(false);
        RaiseStatus(id, from, DownloadStatus.Active);

        // Seed the resume checkpoint from any persisted segments so this run fetches only the missing gaps.
        ReceivedRanges received = await LoadReceivedAsync(id, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<KeyValuePair<string, string>> headers =
            await BuildHeadersAsync(record, cancellationToken).ConfigureAwait(false);
        ProxyConfiguration? proxyOverride = await BuildProxyOverrideAsync(record, cancellationToken)
            .ConfigureAwait(false);
        var downloadRequest = new DownloadRequest
        {
            Url = new Uri(record.Url),
            DestinationPath = Path.Combine(record.Directory, record.Filename),
            Connections = record.MaxConnections,
            SpeedLimit = record.SpeedLimit,
            Headers = headers,
            Proxy = proxyOverride,
        };

        // Periodically flush the checkpoint so a crash loses at most one interval; pause/cancel flushes the
        // exact final offsets below, so a clean pause re-fetches nothing (AC0/AC1).
        using var checkpointCts = new CancellationTokenSource();
        Task checkpointLoop = CheckpointLoopAsync(id, received, checkpointCts.Token);

        // Auto-retry transient (network) failures with exponential backoff, resuming from the checkpoint so
        // only the missing gaps are re-fetched (TASK-131). Permanent failures — auth, expiry, resume-refused
        // — and a user pause are never retried.
        DownloadResult result;
        int retries = 0;
        while (true)
        {
            try
            {
                // Detect an already-expired link and capture the resume validators (ETag/size) before
                // fetching, so a later renew can prove identity (TASK-032).
                active = await PrepareForDownloadAsync(active, headers, proxyOverride, cancellationToken)
                    .ConfigureAwait(false);

                int connections = active.MaxConnections ?? _segmentationOptions.DefaultConnections;
                var estimator = new SpeedEstimator();
                var sink = new ProgressSink(
                    this, id, estimator, active.TotalBytes, active.TotalBytes is > 0, connections);
                var connectionSink = new ConnectionProgressSink(this, id);

                result = await _downloader.DownloadAsync(
                    downloadRequest, sink, received, connectionSink, connections: null, cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A user pause: persist the exact offsets reached, then mark Paused. The partial file and
                // these segment rows let a resume continue without re-fetching.
                await StopCheckpointLoopAsync(checkpointCts, checkpointLoop).ConfigureAwait(false);
                await PersistSegmentsAsync(id, received, CancellationToken.None).ConfigureAwait(false);
                await TransitionToTerminalAsync(id, active, DownloadStatus.Paused, error: null, completedAt: null)
                    .ConfigureAwait(false);
                throw;
            }
            catch (DownloadExpiredException ex)
            {
                // The link expired: keep the checkpoint so a renew with a fresh URL can resume the bytes.
                await StopCheckpointLoopAsync(checkpointCts, checkpointLoop).ConfigureAwait(false);
                await PersistSegmentsAsync(id, received, CancellationToken.None).ConfigureAwait(false);
                await TransitionToTerminalAsync(id, active, DownloadStatus.Expired, ex.Message, completedAt: null)
                    .ConfigureAwait(false);
                LogExpired(_logger, id);
                throw;
            }
            catch (ResourceProbeException ex) when (ExpiryDetection.IsExpiryStatusCode(ex.StatusCode))
            {
                // The probe (e.g. when resuming a download whose validators were already captured) hit an
                // expiry status — surface it as Expired and keep the checkpoint for a renew.
                await StopCheckpointLoopAsync(checkpointCts, checkpointLoop).ConfigureAwait(false);
                await PersistSegmentsAsync(id, received, CancellationToken.None).ConfigureAwait(false);
                await TransitionToTerminalAsync(id, active, DownloadStatus.Expired, ex.Message, completedAt: null)
                    .ConfigureAwait(false);
                LogExpired(_logger, id);
                throw new DownloadExpiredException(ex.Message, ex);
            }
            catch (ResumeNotSupportedException ex)
            {
                // The server rejected the resume offset: the partial bytes are unusable, so drop the
                // checkpoint (the next start is a clean restart from zero) and surface a restart-required
                // failure.
                await StopCheckpointLoopAsync(checkpointCts, checkpointLoop).ConfigureAwait(false);
                await ClearSegmentsAsync(id).ConfigureAwait(false);
                await TransitionToTerminalAsync(id, active, DownloadStatus.Failed, ex.Message, completedAt: null)
                    .ConfigureAwait(false);
                LogFailed(_logger, id, ex);
                throw;
            }
            catch (Exception ex) when (retries < _backoff.MaxRetries && TransientFailure.IsTransient(ex))
            {
                // Transient network glitch: record the retry (persisted), keep the checkpoint, wait the
                // backoff, then loop to resume. The download stays Active across the wait.
                retries++;
                active = active with { RetryCount = retries };
                await _repository.UpdateAsync(active, CancellationToken.None).ConfigureAwait(false);
                await PersistSegmentsAsync(id, received, CancellationToken.None).ConfigureAwait(false);
                LogRetrying(_logger, id, retries, ex);

                try
                {
                    await Task.Delay(_backoff.DelayFor(retries), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Paused while waiting to retry — treat as a clean pause.
                    await StopCheckpointLoopAsync(checkpointCts, checkpointLoop).ConfigureAwait(false);
                    await PersistSegmentsAsync(id, received, CancellationToken.None).ConfigureAwait(false);
                    await TransitionToTerminalAsync(
                        id, active, DownloadStatus.Paused, error: null, completedAt: null).ConfigureAwait(false);
                    throw;
                }
            }
            catch (Exception ex)
            {
                // A permanent failure, or transient with retries exhausted, keeps its checkpoint so a manual
                // retry resumes rather than restarts.
                await StopCheckpointLoopAsync(checkpointCts, checkpointLoop).ConfigureAwait(false);
                await PersistSegmentsAsync(id, received, CancellationToken.None).ConfigureAwait(false);
                await TransitionToTerminalAsync(id, active, DownloadStatus.Failed, ex.Message, completedAt: null)
                    .ConfigureAwait(false);
                LogFailed(_logger, id, ex);
                throw;
            }
        }

        await StopCheckpointLoopAsync(checkpointCts, checkpointLoop).ConfigureAwait(false);
        await ClearSegmentsAsync(id).ConfigureAwait(false); // complete — no resume state to keep

        await TransitionToTerminalAsync(
            id,
            active with { TotalBytes = result.TotalBytes },
            DownloadStatus.Completed,
            error: null,
            completedAt: _clock.UtcNow).ConfigureAwait(false);

        // Final snapshot: 100%, ETA zero, resumable iff the transfer used ranges.
        DownloadProgress done = DownloadProgress.Create(
            DownloadStatus.Completed, result.TotalBytes, result.TotalBytes, 0, !result.SingleConnection,
            result.SingleConnection ? 1 : result.InitialSegments);
        _latest[id] = done;
        ProgressChanged?.Invoke(this, new DownloadProgressChangedEventArgs(id, done));

        LogCompleted(_logger, id, result.TotalBytes);
        return result;
    }

    private async Task TransitionToTerminalAsync(
        long id,
        Download current,
        DownloadStatus to,
        string? error,
        DateTimeOffset? completedAt)
    {
        DownloadStatus fromStatus = DownloadStatusCodes.Parse(current.Status);
        DownloadStateMachine.EnsureCanTransition(fromStatus, to);

        Download updated = current with
        {
            Status = DownloadStatusCodes.ToCode(to),
            Error = error,
            CompletedAt = completedAt,
            CategoryStatus = to == DownloadStatus.Completed ? "Complete" : current.CategoryStatus,
        };

        // Persist on the same token-free path even when the caller's token was cancelled (a pause must
        // still record the paused state).
        await _repository.UpdateAsync(updated, CancellationToken.None).ConfigureAwait(false);

        // The download is no longer running — drop its live per-connection stats so the detail view's
        // Connections tab clears rather than showing a frozen last frame, and its progress-throttle state so
        // the dictionary stays bounded.
        _connections.TryRemove(id, out _);
        _progressThrottle.Forget(id);
        RaiseStatus(id, fromStatus, to);
    }

    public async Task<DownloadResult> RenewAsync(long id, Uri newUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newUrl);

        Download record = await _repository.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No download exists with id {id}.");

        ResourceProbeResult probe;
        try
        {
            IReadOnlyList<KeyValuePair<string, string>> renewHeaders =
                await BuildHeadersAsync(record, cancellationToken).ConfigureAwait(false);
            ProxyConfiguration? renewProxy = await BuildProxyOverrideAsync(record, cancellationToken)
                .ConfigureAwait(false);
            using IDisposable proxyScope = _proxy.BeginDownloadScope(renewProxy);
            probe = await _probe.ProbeAsync(newUrl, renewHeaders, cancellationToken).ConfigureAwait(false);
        }
        catch (ResourceProbeException ex) when (ExpiryDetection.IsExpiryStatusCode(ex.StatusCode))
        {
            // The replacement URL is itself already expired.
            throw new DownloadExpiredException($"The renewed link is also expired (status {ex.StatusCode}).", ex);
        }

        // Resume only when the new resource is provably the same bytes; otherwise drop the checkpoint so the
        // restart is clean (US-13 AC2-3).
        bool sameResource = DownloadIdentity.Matches(record, probe);
        if (!sameResource)
        {
            await ClearSegmentsAsync(id).ConfigureAwait(false);
        }

        Download renewed = record with
        {
            Url = newUrl.ToString(),
            ETag = probe.ETag ?? record.ETag,
            TotalBytes = probe.TotalLength ?? record.TotalBytes,
            Error = null,
        };
        await _repository.UpdateAsync(renewed, cancellationToken).ConfigureAwait(false);
        LogRenewed(_logger, id, sameResource);

        // StartAsync resumes from the (kept) checkpoint on a match, or restarts from zero on a mismatch.
        return await StartAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pre-flight before fetching (TASK-032): fail fast if the signed URL is already past its expiry, and on
    /// the first run capture the resume validators (ETag/size) so a later renew can confirm identity. A probe
    /// that returns an expiry status is surfaced as <see cref="DownloadExpiredException"/>.
    /// </summary>
    private async Task<Download> PrepareForDownloadAsync(
        Download active,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        ProxyConfiguration? proxyOverride,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(active.Url);
        if (ExpiryDetection.IsUrlExpired(uri, _clock.UtcNow))
        {
            throw new DownloadExpiredException("The download link has expired (its signed URL is past its expiry).");
        }

        if (!string.IsNullOrEmpty(active.ETag) || active.TotalBytes is not null)
        {
            return active; // validators already captured on a prior run
        }

        try
        {
            // Probe through the per-download proxy override (TASK-157) so the validator capture uses the same
            // route as the transfer — otherwise an origin reachable only via the override wouldn't be probed.
            using IDisposable proxyScope = _proxy.BeginDownloadScope(proxyOverride);
            ResourceProbeResult probe = await _probe.ProbeAsync(uri, headers, cancellationToken).ConfigureAwait(false);
            Download withValidators = active with { ETag = probe.ETag, TotalBytes = probe.TotalLength };
            await _repository.UpdateAsync(withValidators, cancellationToken).ConfigureAwait(false);
            return withValidators;
        }
        catch (ResourceProbeException ex) when (ExpiryDetection.IsExpiryStatusCode(ex.StatusCode))
        {
            throw new DownloadExpiredException($"The download link has expired (status {ex.StatusCode}).", ex);
        }
        catch (ResourceProbeException)
        {
            // A non-expiry probe failure: proceed and let the downloader surface the real error.
            return active;
        }
    }

    /// <summary>
    /// Builds the per-download request headers: the <c>Referer</c> (from the persisted referrer) and, when the
    /// download carries captured cookies, a <c>Cookie</c> header resolved from the OS keychain (TASK-091).
    /// Resolving on every start/resume keeps the cookies out of SQLite while still authenticating the request.
    /// </summary>
    private async Task<IReadOnlyList<KeyValuePair<string, string>>> BuildHeadersAsync(
        Download record, CancellationToken cancellationToken)
    {
        var headers = new List<KeyValuePair<string, string>>(capacity: 2);
        if (record.Referrer is { Length: > 0 } referrer)
        {
            headers.Add(new KeyValuePair<string, string>("Referer", referrer));
        }

        if (record.CookieSecretRef is { Length: > 0 } cookieRef)
        {
            string? cookies = await _secretStore.RetrieveAsync(cookieRef, cancellationToken).ConfigureAwait(false);
            if (cookies is { Length: > 0 })
            {
                headers.Add(new KeyValuePair<string, string>("Cookie", cookies));
            }
        }

        return headers;
    }

    /// <summary>
    /// Stores a per-download proxy override's password in the OS keychain (§5) and returns the column values to
    /// persist (TASK-153). A <see langword="null"/> or disabled override persists all-null (use the global proxy).
    /// </summary>
    private async Task<ProxyOverridePersistence> PersistProxyOverrideAsync(
        ProxyConfiguration? proxy, CancellationToken cancellationToken)
    {
        if (proxy is not { IsEnabled: true })
        {
            return default;
        }

        string? username = string.IsNullOrWhiteSpace(proxy.Credentials?.Username)
            ? null
            : proxy.Credentials.Username;
        string? domain = string.IsNullOrWhiteSpace(proxy.Credentials?.Domain) ? null : proxy.Credentials.Domain;
        string? passwordSecretRef = null;
        if (username is not null && proxy.Credentials!.Password is { Length: > 0 } password)
        {
            passwordSecretRef = await _secretStore.StoreAsync(password, cancellationToken).ConfigureAwait(false);
        }

        return new ProxyOverridePersistence(
            (int)proxy.Kind, proxy.Host, proxy.Port, username, domain, passwordSecretRef);
    }

    /// <summary>
    /// Rebuilds the per-download proxy override from the persisted columns, resolving the password from the OS
    /// keychain (TASK-153). Returns <see langword="null"/> when there is no override (use the global proxy).
    /// </summary>
    private async Task<ProxyConfiguration?> BuildProxyOverrideAsync(
        Download record, CancellationToken cancellationToken)
    {
        if (record.ProxyKind is not { } kindValue)
        {
            return null;
        }

        var kind = (ProxyKind)kindValue;
        if (kind == ProxyKind.None || string.IsNullOrEmpty(record.ProxyHost))
        {
            return null;
        }

        NetworkCredentials? credentials = null;
        if (record.ProxyUsername is { Length: > 0 } username)
        {
            string? password = record.ProxyPasswordSecretRef is { Length: > 0 } secretRef
                ? await _secretStore.RetrieveAsync(secretRef, cancellationToken).ConfigureAwait(false)
                : null;
            credentials = new NetworkCredentials(username, password ?? string.Empty, record.ProxyDomain);
        }

        return new ProxyConfiguration(kind, record.ProxyHost, record.ProxyPort ?? 0, credentials);
    }

    private readonly record struct ProxyOverridePersistence(
        int? Kind, string? Host, int? Port, string? Username, string? Domain, string? PasswordSecretRef);

    /// <summary>Rebuilds the resume checkpoint from persisted segment rows (empty for a fresh download).</summary>
    private async Task<ReceivedRanges> LoadReceivedAsync(long id, CancellationToken cancellationToken)
    {
        IReadOnlyList<DownloadSegment> rows =
            await _segments.GetByDownloadAsync(id, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return new ReceivedRanges();
        }

        return new ReceivedRanges(rows.Select(r => new ByteInterval(r.Start, r.End)));
    }

    /// <summary>
    /// Replaces the download's persisted segment rows with the current coalesced received intervals — the
    /// checkpoint a resume reads. The snapshot is taken durably (the output file is fsynced before the
    /// offsets are recorded, TASK-109) so a power loss can never leave the checkpoint ahead of the bytes
    /// actually on disk, and it is applied as a single atomic delete-then-multi-row-insert so the row set
    /// exactly mirrors the file and the 500ms checkpoint costs a constant few queries (TASK-103).
    /// </summary>
    private async Task PersistSegmentsAsync(long id, ReceivedRanges received, CancellationToken cancellationToken)
    {
        IReadOnlyList<ByteInterval> intervals =
            await received.SnapshotDurableAsync(cancellationToken).ConfigureAwait(false);

        var rows = new DownloadSegment[intervals.Count];
        for (int i = 0; i < intervals.Count; i++)
        {
            ByteInterval interval = intervals[i];
            rows[i] = new DownloadSegment
            {
                DownloadId = id,
                Index = i,
                Start = interval.Start,
                End = interval.EndInclusive,
                Downloaded = interval.Length,
                State = "complete",
            };
        }

        await _segments.ReplaceForDownloadAsync(id, rows, cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearSegmentsAsync(long id) =>
        await _segments.DeleteByDownloadAsync(id, CancellationToken.None).ConfigureAwait(false);

    private async Task CheckpointLoopAsync(long id, ReceivedRanges received, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(CheckpointInterval, token).ConfigureAwait(false);
                await PersistSegmentsAsync(id, received, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the download ends; the caller persists the final checkpoint.
        }
    }

    private static async Task StopCheckpointLoopAsync(CancellationTokenSource cts, Task loop)
    {
        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RaiseStatus(long id, DownloadStatus? previous, DownloadStatus current) =>
        StatusChanged?.Invoke(this, new DownloadStatusChangedEventArgs(id, previous, current));

    private void OnBytes(
        long id, SpeedEstimator estimator, long? total, bool resumable, int connections, long cumulativeBytes)
    {
        DateTimeOffset now = _clock.UtcNow;
        double speed = estimator.Sample(now, cumulativeBytes);
        DownloadProgress snapshot = DownloadProgress.Create(
            DownloadStatus.Active, cumulativeBytes, total, speed, resumable, connections);

        // Keep the latest snapshot current for GetProgress and the explicit terminal emit, but only notify the
        // UI at a bounded rate so a fast transfer's per-chunk reports don't flood the UI thread (TASK-104).
        _latest[id] = snapshot;
        if (_progressThrottle.ShouldEmit(id, now))
        {
            ProgressChanged?.Invoke(this, new DownloadProgressChangedEventArgs(id, snapshot));
        }
    }

    private void OnConnectionProgress(long id, ConnectionProgress progress) =>
        _connections.GetOrAdd(id, static _ => new ConnectionTracker()).Update(_clock, progress);

    /// <summary>
    /// A synchronous <see cref="IProgress{T}"/> bridge: the segmented downloader reports cumulative bytes
    /// from its worker threads, and each report is turned into a progress snapshot. Using a direct
    /// implementation (rather than <see cref="Progress{T}"/>) keeps the callback on the reporting thread so
    /// the speed estimator sees samples in close to real order.
    /// </summary>
    private sealed class ProgressSink : IProgress<long>
    {
        private readonly DownloadManager _owner;
        private readonly long _id;
        private readonly SpeedEstimator _estimator;
        private readonly long? _total;
        private readonly bool _resumable;
        private readonly int _connections;

        public ProgressSink(
            DownloadManager owner, long id, SpeedEstimator estimator, long? total, bool resumable, int connections)
        {
            _owner = owner;
            _id = id;
            _estimator = estimator;
            _total = total;
            _resumable = resumable;
            _connections = connections;
        }

        public void Report(long value) =>
            _owner.OnBytes(_id, _estimator, _total, _resumable, _connections, value);
    }

    /// <summary>
    /// Bridges the downloader's per-connection reports into the owning manager's <see cref="ConnectionTracker"/>
    /// for one download (TASK-054). Direct <see cref="IProgress{T}"/> so the fold runs on the reporting worker
    /// thread, keeping each connection's speed samples close to real order (mirrors <see cref="ProgressSink"/>).
    /// </summary>
    private sealed class ConnectionProgressSink : IProgress<ConnectionProgress>
    {
        private readonly DownloadManager _owner;
        private readonly long _id;

        public ConnectionProgressSink(DownloadManager owner, long id)
        {
            _owner = owner;
            _id = id;
        }

        public void Report(ConnectionProgress value) => _owner.OnConnectionProgress(_id, value);
    }

    /// <summary>
    /// Folds a download's stream of per-connection reports into live <see cref="ConnectionStat"/>s (TASK-054).
    /// Each connection keeps its own <see cref="SpeedEstimator"/> fed by a cumulative byte count that survives
    /// work-steals (a new segment continues the same connection's total), so the derived speed is per
    /// connection, not per segment. Thread-safe: reports arrive concurrently from every worker thread.
    /// </summary>
    private sealed class ConnectionTracker
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, ConnectionState> _byConnection = [];

        public void Update(IClock clock, ConnectionProgress progress)
        {
            lock (_gate)
            {
                if (!_byConnection.TryGetValue(progress.ConnectionId, out ConnectionState? state))
                {
                    state = new ConnectionState { LastSegmentIndex = progress.SegmentIndex, LastPosition = progress.Start };
                    _byConnection[progress.ConnectionId] = state;
                }

                // Accumulate this connection's lifetime bytes from per-report deltas. Within one segment the
                // delta is the cursor advance; when the connection steals a new segment its cursor jumps, so
                // count only the bytes written into the new segment so far.
                long delta = progress.SegmentIndex == state.LastSegmentIndex
                    ? progress.Position - state.LastPosition
                    : progress.SegmentDownloaded;
                if (delta > 0)
                {
                    state.Cumulative += delta;
                }

                state.LastSegmentIndex = progress.SegmentIndex;
                state.LastPosition = progress.Position;
                state.Latest = progress;
                state.Active = !progress.IsComplete;
                state.Speed = progress.IsComplete ? 0 : state.Estimator.Sample(clock.UtcNow, state.Cumulative);
            }
        }

        public List<ConnectionStat> Snapshot()
        {
            lock (_gate)
            {
                var stats = new List<ConnectionStat>(_byConnection.Count);
                foreach (ConnectionState state in _byConnection.Values)
                {
                    ConnectionProgress latest = state.Latest;
                    stats.Add(new ConnectionStat
                    {
                        ConnectionId = latest.ConnectionId,
                        SegmentIndex = latest.SegmentIndex,
                        Start = latest.Start,
                        End = latest.End,
                        DownloadedBytes = latest.SegmentDownloaded,
                        TotalBytes = latest.SegmentTotal,
                        BytesPerSecond = state.Speed,
                        IsActive = state.Active,
                    });
                }

                stats.Sort(static (a, b) => a.ConnectionId.CompareTo(b.ConnectionId));
                return stats;
            }
        }

        private sealed class ConnectionState
        {
            public SpeedEstimator Estimator { get; } = new();

            public long Cumulative { get; set; }

            public int LastSegmentIndex { get; set; }

            public long LastPosition { get; set; }

            public ConnectionProgress Latest { get; set; } = new()
            {
                ConnectionId = 0,
                SegmentIndex = 0,
                Start = 0,
                End = 0,
                Position = 0,
            };

            public double Speed { get; set; }

            public bool Active { get; set; } = true;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Enqueued download {Id} from {Source}.")]
    private static partial void LogEnqueued(ILogger logger, long id, string source);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Download {Id} completed ({Bytes} bytes).")]
    private static partial void LogCompleted(ILogger logger, long id, long bytes);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Download {Id} failed.")]
    private static partial void LogFailed(ILogger logger, long id, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Download {Id} link expired; awaiting renew.")]
    private static partial void LogExpired(ILogger logger, long id);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Download {Id} renewed (sameResource={SameResource}).")]
    private static partial void LogRenewed(ILogger logger, long id, bool sameResource);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Warning,
        Message = "Download {Id} hit a transient failure; retry {Retry} after backoff.")]
    private static partial void LogRetrying(ILogger logger, long id, int retry, Exception exception);
}
