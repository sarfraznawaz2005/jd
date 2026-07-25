namespace JustDownload.Core.Abstractions;

/// <summary>
/// A monotonic time source for measuring *elapsed* time. Unlike <see cref="IClock"/> — wall-clock time, which
/// an NTP correction or a VM host time-sync can step or slew forwards and backwards — this only ever advances,
/// and at the same rate as real time. Anything whose maths depends on how much time has passed (rate limiting)
/// must use this; anything that needs an actual point in time (timestamps, URL expiry) uses <see cref="IClock"/>.
/// </summary>
public interface IMonotonicClock
{
    /// <summary>Time elapsed since an arbitrary fixed origin. Only differences between readings are meaningful.</summary>
    TimeSpan Elapsed { get; }
}
