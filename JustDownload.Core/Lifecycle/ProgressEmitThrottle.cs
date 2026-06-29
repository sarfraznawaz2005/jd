using System.Collections.Concurrent;

namespace JustDownload.Core.Lifecycle;

/// <summary>
/// Rate-limits per-download progress notifications. A fast transfer reports hundreds of byte-count chunks a
/// second; forwarding every one to the UI marshals to the UI thread and rebuilds the connection list each
/// time, dropping frames on slow hardware (D9). This gates emission to at most one notification per
/// <see cref="_minInterval"/> per download — the first report for a download always passes, and the latest
/// snapshot is always kept current by the caller, so coalescing never loses final-state accuracy (the
/// terminal snapshot is emitted outside this throttle). Thread-safe; the gate is a soft bound, so the rare
/// race where two worker threads both pass within one window is harmless.
/// </summary>
internal sealed class ProgressEmitThrottle
{
    private readonly TimeSpan _minInterval;
    private readonly ConcurrentDictionary<long, DateTimeOffset> _lastEmit = new();

    public ProgressEmitThrottle(TimeSpan minInterval) => _minInterval = minInterval;

    /// <summary>
    /// Returns <see langword="true"/> (and records <paramref name="now"/>) when a notification for
    /// <paramref name="id"/> may be emitted — the first ever, or once at least the minimum interval has
    /// elapsed since the last emit. Returns <see langword="false"/> to coalesce an in-window report.
    /// </summary>
    public bool ShouldEmit(long id, DateTimeOffset now)
    {
        if (_lastEmit.TryGetValue(id, out DateTimeOffset last) && now - last < _minInterval)
        {
            return false;
        }

        _lastEmit[id] = now;
        return true;
    }

    /// <summary>Drops a finished download's throttle state so the dictionary stays bounded.</summary>
    public void Forget(long id) => _lastEmit.TryRemove(id, out _);
}
