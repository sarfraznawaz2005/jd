namespace JustDownload.Core.Abstractions;

/// <summary>
/// Abstracts the system clock so time-dependent engine logic (retry back-off, token-bucket
/// throttling, URL-expiry checks) stays deterministic and unit-testable. Inject this instead
/// of calling <see cref="DateTimeOffset.UtcNow"/> directly.
/// </summary>
public interface IClock
{
    /// <summary>The current UTC time.</summary>
    DateTimeOffset UtcNow { get; }
}
