using JustDownload.App.ViewModels;
using JustDownload.Core.Settings;

namespace JustDownload.App.Services;

/// <summary>
/// Default <see cref="IYouTubeSignInConsentGate"/>. Skips the dialog once
/// <see cref="AppSettings.YouTubeSignInConsentAcknowledged"/> is set; otherwise shows it through the
/// injected <paramref name="showNotice"/> function and persists the acknowledgment. Mirrors
/// <see cref="TosNoticeGate"/> exactly, including why the dialog is injected rather than constructed here
/// (keeps this settings/persist logic unit-testable without a live window).
/// </summary>
public sealed class YouTubeSignInConsentGate : IYouTubeSignInConsentGate
{
    private readonly ISettingsService _settings;
    private readonly Func<CancellationToken, Task<YouTubeSignInConsentResult>> _showNotice;

    public YouTubeSignInConsentGate(
        ISettingsService settings, Func<CancellationToken, Task<YouTubeSignInConsentResult>> showNotice)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(showNotice);
        _settings = settings;
        _showNotice = showNotice;
    }

    public async Task<bool> ConfirmAsync(CancellationToken cancellationToken = default)
    {
        if (_settings.Current.YouTubeSignInConsentAcknowledged)
        {
            return true;
        }

        YouTubeSignInConsentResult result = await _showNotice(cancellationToken).ConfigureAwait(true);
        if (result == YouTubeSignInConsentResult.Continue)
        {
            await _settings.UpdateAsync(s => s with { YouTubeSignInConsentAcknowledged = true }, cancellationToken)
                .ConfigureAwait(true);
            return true;
        }

        return false;
    }
}
