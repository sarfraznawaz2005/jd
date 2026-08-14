using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Transport;

/// <summary>
/// Keeps <see cref="TransportOptions.UserAgent"/> pointed at a real, currently-installed browser's User-Agent
/// instead of the static hardcoded fallback baked into <see cref="TransportOptions"/>. Tries Chrome, then
/// Edge, then Firefox (<see cref="BrowserUserAgentDetector"/>); the result is cached for
/// <see cref="CacheMaxAge"/> so a normal launch only reads a small file rather than re-probing installed
/// browsers, and falls back to the static default when none of the three are found.
/// </summary>
public interface IBrowserUserAgentRefresher
{
    /// <summary>Applies the cached or freshly-detected User-Agent to the shared <see cref="TransportOptions"/>.
    /// Best-effort: never throws — a detection failure just leaves the existing (static default) UA in place.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}

internal sealed partial class BrowserUserAgentRefresher : IBrowserUserAgentRefresher
{
    /// <summary>How long a detected User-Agent is trusted before it's re-probed ("fresh for at least 1 month").</summary>
    internal static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(30);

    private readonly TransportOptions _options;
    private readonly IBrowserUserAgentDetector _detector;
    private readonly IBrowserUserAgentCache _cache;
    private readonly ILogger<BrowserUserAgentRefresher> _logger;

    public BrowserUserAgentRefresher(
        TransportOptions options,
        IBrowserUserAgentDetector detector,
        IBrowserUserAgentCache cache,
        ILogger<BrowserUserAgentRefresher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _detector = detector;
        _cache = cache;
        _logger = logger;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string? cached = await _cache.TryReadFreshAsync(CacheMaxAge, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                _options.UserAgent = cached;
                return;
            }

            (BrowserKind Kind, string Version)? detected =
                await _detector.TryDetectAsync(cancellationToken).ConfigureAwait(false);
            if (detected is null)
            {
                return; // no installed browser found; TransportOptions keeps its static default UA
            }

            string userAgent = BrowserUserAgentFormatter.Build(detected.Value.Kind, detected.Value.Version);
            _options.UserAgent = userAgent;
            await _cache.WriteAsync(userAgent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort background refresh (called fire-and-forget at startup, CLAUDE.md App.axaml.cs
            // pattern) — a failure here must never take down the app; it just leaves TransportOptions on its
            // existing UA. Logged rather than swallowed outright (no silent failures, CLAUDE.md §1).
            LogRefreshFailed(_logger, ex);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Failed to refresh the browser-based default User-Agent; keeping the existing one.")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception);
}
