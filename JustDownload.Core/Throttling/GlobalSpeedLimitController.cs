using JustDownload.Core.Abstractions;
using JustDownload.Core.Settings;

namespace JustDownload.Core.Throttling;

/// <summary>
/// Keeps the shared global <see cref="IRateLimiter"/> in sync with the persisted speed settings (US-3) — the
/// single writer of the limiter's rate. The effective cap is the active time-of-day rule
/// (<see cref="AppSettings.BandwidthSchedule"/>, TASK-145) when one matches the current local time, otherwise
/// the manual <see cref="AppSettings.GlobalSpeedLimitBytesPerSecond"/>.
/// <para>
/// A host calls <see cref="ApplyCurrent"/> once after settings are loaded and <see cref="Start"/> to begin
/// re-evaluating on a timer so a schedule boundary crossing applies automatically; every persisted change is
/// also applied live, so raising or lowering the cap takes effect on the next chunk without a restart.
/// </para>
/// </summary>
public sealed class GlobalSpeedLimitController : IDisposable
{
    private static readonly TimeSpan ReevaluateInterval = TimeSpan.FromMinutes(1);

    private readonly ISettingsService _settings;
    private readonly IRateLimiter _rateLimiter;
    private readonly IClock _clock;
    private Timer? _timer;
    private bool _disposed;

    public GlobalSpeedLimitController(ISettingsService settings, IRateLimiter rateLimiter, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(rateLimiter);
        ArgumentNullException.ThrowIfNull(clock);
        _settings = settings;
        _rateLimiter = rateLimiter;
        _clock = clock;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Applies the effective limit (schedule or manual) to the shared limiter. Call once after load.</summary>
    public void ApplyCurrent() => Apply();

    /// <summary>Begins re-evaluating the schedule on a timer so boundary crossings apply automatically (TASK-145).</summary>
    public void Start()
    {
        _timer ??= new Timer(_ => Apply(), state: null, ReevaluateInterval, ReevaluateInterval);
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) => Apply();

    private void Apply()
    {
        AppSettings settings = _settings.Current;
        IReadOnlyList<BandwidthRule> rules = BandwidthSchedule.Parse(settings.BandwidthSchedule);
        var now = TimeOnly.FromDateTime(_clock.UtcNow.ToLocalTime().DateTime);
        _rateLimiter.BytesPerSecond =
            BandwidthSchedule.EffectiveLimit(rules, now, settings.GlobalSpeedLimitBytesPerSecond);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _timer?.Dispose();
    }
}
