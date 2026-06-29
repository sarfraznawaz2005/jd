using JustDownload.Core.Security;
using JustDownload.Core.Settings;
using JustDownload.Core.Transport.Auth;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Transport.Proxy;

/// <summary>
/// Keeps the engine's global <see cref="IProxyService"/> in sync with the persisted proxy settings
/// (TASK-125). Without this the Proxy settings panel would be a dead setting — saved but never routed
/// through. The auth password is resolved from the OS keychain on demand (§5), so the plaintext never lives
/// in settings.
/// <para>
/// A host calls <see cref="ApplyCurrentAsync"/> once after settings load (the load doesn't raise
/// <see cref="ISettingsService.Changed"/>); thereafter every persisted change is applied live, so toggling
/// or editing the proxy takes effect on the next request without a restart (AC2).
/// </para>
/// </summary>
public sealed partial class GlobalProxyController : IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IProxyService _proxy;
    private readonly ISecretStore _secrets;
    private readonly ILogger<GlobalProxyController> _logger;

    public GlobalProxyController(
        ISettingsService settings,
        IProxyService proxy,
        ISecretStore secrets,
        ILogger<GlobalProxyController> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _proxy = proxy;
        _secrets = secrets;
        _logger = logger;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Applies the currently-loaded proxy settings to the engine. Call once after load.</summary>
    public Task ApplyCurrentAsync(CancellationToken cancellationToken = default) =>
        ApplyAsync(_settings.Current, cancellationToken);

    private async void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        try
        {
            await ApplyAsync(e.Current, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failure to resolve the proxy password must not crash the settings-change notification.
            LogApplyFailed(_logger, ex);
        }
    }

    private async Task ApplyAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ProxyConfiguration config = ProxyConfiguration.None;
        if (settings.ProxyKind != ProxyKind.None && !string.IsNullOrWhiteSpace(settings.ProxyHost))
        {
            NetworkCredentials? credentials = null;
            if (!string.IsNullOrWhiteSpace(settings.ProxyUsername))
            {
                string? password = settings.ProxyPasswordSecretRef is { Length: > 0 } secretRef
                    ? await _secrets.RetrieveAsync(secretRef, cancellationToken).ConfigureAwait(false)
                    : null;
                string? domain = string.IsNullOrWhiteSpace(settings.ProxyDomain) ? null : settings.ProxyDomain;
                credentials = new NetworkCredentials(settings.ProxyUsername!, password ?? string.Empty, domain);
            }

            config = new ProxyConfiguration(settings.ProxyKind, settings.ProxyHost, settings.ProxyPort, credentials);
        }

        _proxy.SetGlobalProxy(config);
    }

    public void Dispose() => _settings.Changed -= OnSettingsChanged;

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Failed to apply the proxy settings; keeping the previous proxy configuration.")]
    private static partial void LogApplyFailed(ILogger logger, Exception exception);
}
