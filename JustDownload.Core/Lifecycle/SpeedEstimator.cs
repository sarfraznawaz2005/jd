namespace JustDownload.Core.Lifecycle;

/// <summary>
/// A small, deterministic sliding-window speed estimator (TASK-031). Fed timestamped cumulative byte counts,
/// it reports the average rate over the most recent <see cref="_window"/> of samples — a window smooths the
/// burstiness of segmented writes while still tracking real changes (a pause or a slowdown decays out within
/// one window). It is driven by an injected clock's timestamps, so it is fully reproducible in tests with no
/// dependency on wall-clock time.
/// <para>
/// Thread-safe: the segmented downloader reports progress from several worker threads, so samples may arrive
/// concurrently and slightly out of order. Out-of-order (lower) cumulative counts are ignored, and all state
/// is guarded by a lock.
/// </para>
/// </summary>
public sealed class SpeedEstimator
{
    private readonly TimeSpan _window;
    private readonly Queue<Reading> _samples = new();
    private readonly object _gate = new();
    private long _lastBytes = -1;

    /// <summary>Creates an estimator averaging over <paramref name="window"/> (default 3 seconds).</summary>
    public SpeedEstimator(TimeSpan? window = null)
    {
        TimeSpan w = window ?? TimeSpan.FromSeconds(3);
        if (w <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), w, "The averaging window must be positive.");
        }

        _window = w;
    }

    /// <summary>
    /// Records a cumulative byte count observed at <paramref name="now"/> and returns the current estimated
    /// rate in bytes/second. Samples whose count is not greater than the last accepted one are ignored
    /// (they are stale, out-of-order reports) and return the existing estimate.
    /// </summary>
    public double Sample(DateTimeOffset now, long cumulativeBytes)
    {
        lock (_gate)
        {
            if (cumulativeBytes <= _lastBytes)
            {
                return CurrentRateLocked(now);
            }

            _lastBytes = cumulativeBytes;
            _samples.Enqueue(new Reading(now, cumulativeBytes));
            Evict(now);
            return CurrentRateLocked(now);
        }
    }

    /// <summary>The most recently computed rate in bytes/second, relative to <paramref name="now"/>.</summary>
    public double CurrentRate(DateTimeOffset now)
    {
        lock (_gate)
        {
            Evict(now);
            return CurrentRateLocked(now);
        }
    }

    private void Evict(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - _window;
        // Keep one sample at or before the cutoff so the window always spans a measurable interval.
        while (_samples.Count > 2 && _samples.Peek().Time < cutoff)
        {
            _samples.Dequeue();
        }
    }

    private double CurrentRateLocked(DateTimeOffset now)
    {
        if (_samples.Count < 2)
        {
            return 0;
        }

        Reading oldest = _samples.Peek();
        double seconds = (now - oldest.Time).TotalSeconds;
        if (seconds <= 0)
        {
            return 0;
        }

        return (_lastBytes - oldest.Bytes) / seconds;
    }

    private readonly record struct Reading(DateTimeOffset Time, long Bytes);
}
