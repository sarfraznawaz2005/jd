using JustDownload.Core.Settings;

namespace JustDownload.Core.Lifecycle;

/// <summary>
/// The auto-retry budget and backoff schedule for a transient download failure (TASK-131). Abstracted so the
/// lifecycle can compute "how many retries" and "how long to wait" without hard-coding either, and so tests
/// can substitute a zero-delay schedule.
/// </summary>
public interface IRetryBackoff
{
    /// <summary>Maximum auto-retries before a transient failure becomes a permanent one. <c>0</c> disables retry.</summary>
    int MaxRetries { get; }

    /// <summary>The delay to wait before the <paramref name="retryNumber"/>th retry (1-based).</summary>
    TimeSpan DelayFor(int retryNumber);
}

/// <summary>
/// Default <see cref="IRetryBackoff"/>: exponential backoff (1s, 2s, 4s, …) capped at 30s, with the retry
/// count read live from <see cref="AppSettings.MaxDownloadRetries"/> so changing the setting takes effect
/// without a restart.
/// </summary>
internal sealed class ExponentialBackoff : IRetryBackoff
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    private readonly ISettingsService _settings;

    public ExponentialBackoff(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public int MaxRetries => Math.Max(0, _settings.Current.MaxDownloadRetries);

    public TimeSpan DelayFor(int retryNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retryNumber);
        double seconds = BaseDelay.TotalSeconds * Math.Pow(2, retryNumber - 1);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxDelay.TotalSeconds));
    }
}
