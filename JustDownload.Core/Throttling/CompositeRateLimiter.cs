namespace JustDownload.Core.Throttling;

/// <summary>
/// Applies several <see cref="IRateLimiter"/>s at once (TASK-030): the download copy loop acquires from
/// all of them, so the effective rate is bounded by the strictest cap. The engine uses this to enforce a
/// per-download cap and the shared global cap simultaneously. <see cref="BytesPerSecond"/> reflects the
/// tightest non-unlimited inner cap.
/// </summary>
public sealed class CompositeRateLimiter : IRateLimiter
{
    private readonly IReadOnlyList<IRateLimiter> _limiters;

    public CompositeRateLimiter(params IRateLimiter[] limiters)
    {
        ArgumentNullException.ThrowIfNull(limiters);
        _limiters = [.. limiters];
    }

    /// <summary>The tightest effective cap across the inner limiters; <c>0</c> if all are unlimited.</summary>
    public long BytesPerSecond
    {
        get
        {
            long tightest = 0;
            foreach (IRateLimiter limiter in _limiters)
            {
                long rate = limiter.BytesPerSecond;
                if (rate > 0 && (tightest == 0 || rate < tightest))
                {
                    tightest = rate;
                }
            }

            return tightest;
        }

        set
        {
            foreach (IRateLimiter limiter in _limiters)
            {
                limiter.BytesPerSecond = value;
            }
        }
    }

    public async ValueTask AcquireAsync(int count, CancellationToken cancellationToken = default)
    {
        foreach (IRateLimiter limiter in _limiters)
        {
            await limiter.AcquireAsync(count, cancellationToken).ConfigureAwait(false);
        }
    }
}
