using JustDownload.Core.Settings;

namespace JustDownload.Core.Throttling;

/// <summary>
/// Keeps the shared global <see cref="IRateLimiter"/> in sync with the persisted
/// <see cref="AppSettings.GlobalSpeedLimitBytesPerSecond"/> (US-3). Without this, the engine's global cap
/// would stay at its constructed default (unlimited) and the Connections-settings speed limit would be a
/// dead setting — saved but never enforced.
/// <para>
/// A host calls <see cref="ApplyCurrent"/> once after settings are loaded (the load doesn't raise
/// <see cref="ISettingsService.Changed"/>); thereafter every persisted change is applied live, so raising
/// or lowering the cap takes effect on the next chunk without a restart.
/// </para>
/// </summary>
public sealed class GlobalSpeedLimitController : IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IRateLimiter _rateLimiter;

    public GlobalSpeedLimitController(ISettingsService settings, IRateLimiter rateLimiter)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(rateLimiter);
        _settings = settings;
        _rateLimiter = rateLimiter;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Applies the currently-loaded global speed limit to the shared limiter. Call once after load.</summary>
    public void ApplyCurrent() => Apply(_settings.Current.GlobalSpeedLimitBytesPerSecond);

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) =>
        Apply(e.Current.GlobalSpeedLimitBytesPerSecond);

    private void Apply(long bytesPerSecond) => _rateLimiter.BytesPerSecond = bytesPerSecond;

    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}
