namespace JustDownload.Core.Throttling;

/// <summary>
/// A bandwidth limiter the download copy loops await before writing each chunk (TASK-030, US-3). The
/// engine composes a per-download limiter with the global one so both caps apply at once. The cap is
/// <see cref="BytesPerSecond"/>; <c>0</c> means unlimited (no throttling). The rate can be changed at any
/// time and takes effect on the next chunk, so a user can raise or lower a download's speed mid-flight.
/// </summary>
public interface IRateLimiter
{
    /// <summary>The cap in bytes per second; <c>0</c> means unlimited. Settable while downloading.</summary>
    long BytesPerSecond { get; set; }

    /// <summary>
    /// Waits until <paramref name="count"/> bytes may be sent under the current rate, then returns. Returns
    /// immediately when the limiter is unlimited or <paramref name="count"/> is non-positive.
    /// </summary>
    ValueTask AcquireAsync(int count, CancellationToken cancellationToken = default);
}
