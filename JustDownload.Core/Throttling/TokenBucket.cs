using JustDownload.Core.Abstractions;

namespace JustDownload.Core.Throttling;

/// <summary>
/// A token-bucket <see cref="IRateLimiter"/> (TASK-030, CLAUDE.md §3). Tokens (one per byte) accrue at
/// <see cref="BytesPerSecond"/> up to a one-second burst capacity; acquiring waits until enough have
/// accrued. Refill timing uses the injected <see cref="IMonotonicClock"/>, so the reservation maths
/// (<see cref="Reserve"/>) is deterministic and unit-testable without real delays. Thread-safe: many
/// segment connections share one bucket (the global cap) and acquire from it concurrently.
/// <para>
/// The time source is deliberately monotonic rather than wall-clock: refill is a measurement of *elapsed*
/// time, and a wall clock can be stepped or slewed by NTP / VM host time-sync. A forward correction would
/// mint tokens the transfer never earned (letting a download briefly exceed its cap), and a backward one
/// would stall every acquire until the clock caught up.
/// </para>
/// </summary>
public sealed class TokenBucket : IRateLimiter
{
    /// <summary>Burst floor so a single download chunk can always be acquired even at very low rates.</summary>
    public const long MinimumCapacity = 64 * 1024;

    private readonly IMonotonicClock _clock;
    private readonly object _gate = new();
    private long _bytesPerSecond;
    private long _capacity;
    private double _tokens;
    private TimeSpan _lastRefill;

    public TokenBucket(IMonotonicClock clock, long bytesPerSecond = 0)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegative(bytesPerSecond);
        _clock = clock;
        _lastRefill = clock.Elapsed;
        SetRate(bytesPerSecond);
    }

    public long BytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                return _bytesPerSecond;
            }
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            lock (_gate)
            {
                Refill();
                SetRate(value);
            }
        }
    }

    public async ValueTask AcquireAsync(int count, CancellationToken cancellationToken = default)
    {
        if (count <= 0)
        {
            return;
        }

        while (true)
        {
            TimeSpan wait = Reserve(count);
            if (wait <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Attempts to reserve <paramref name="count"/> tokens. Returns <see cref="TimeSpan.Zero"/> (and
    /// consumes them) when enough are available now or the bucket is unlimited; otherwise returns the time
    /// to wait before they will be, consuming nothing. Pure given the clock — the heart of the throttle.
    /// </summary>
    internal TimeSpan Reserve(int count)
    {
        lock (_gate)
        {
            if (_bytesPerSecond == 0)
            {
                return TimeSpan.Zero; // unlimited
            }

            Refill();
            if (_tokens >= count)
            {
                _tokens -= count;
                return TimeSpan.Zero;
            }

            double deficit = count - _tokens;
            return TimeSpan.FromSeconds(deficit / _bytesPerSecond);
        }
    }

    private void SetRate(long bytesPerSecond)
    {
        _bytesPerSecond = bytesPerSecond;
        _capacity = bytesPerSecond == 0 ? 0 : Math.Max(bytesPerSecond, MinimumCapacity);
        if (_tokens > _capacity)
        {
            _tokens = _capacity;
        }
    }

    private void Refill()
    {
        TimeSpan now = _clock.Elapsed;
        double elapsedSeconds = (now - _lastRefill).TotalSeconds;
        if (elapsedSeconds <= 0)
        {
            return;
        }

        _lastRefill = now;
        if (_bytesPerSecond > 0)
        {
            _tokens = Math.Min(_capacity, _tokens + (elapsedSeconds * _bytesPerSecond));
        }
    }
}
