using JustDownload.App.ViewModels;
using JustDownload.Core.Settings;

namespace JustDownload.App.Services;

/// <summary>
/// Default <see cref="ITosNoticeGate"/> (TASK-160). Skips the dialog entirely once
/// <see cref="AppSettings.SuppressTosNotice"/> is set; otherwise shows it through the injected
/// <paramref name="showNotice"/> function and persists suppression when the user picks "Don't show this
/// again". The dialog itself is injected (rather than constructed here) so this settings/persist logic stays
/// unit-testable without a live window — the real function (wired in <c>App.axaml.cs</c>) shows
/// <see cref="JustDownload.App.Views.TosNoticeWindow"/> over the active window.
/// </summary>
public sealed class TosNoticeGate : ITosNoticeGate
{
    private readonly ISettingsService _settings;
    private readonly Func<CancellationToken, Task<TosNoticeResult>> _showNotice;

    public TosNoticeGate(ISettingsService settings, Func<CancellationToken, Task<TosNoticeResult>> showNotice)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(showNotice);
        _settings = settings;
        _showNotice = showNotice;
    }

    public async Task<bool> ConfirmAsync(CancellationToken cancellationToken = default)
    {
        if (_settings.Current.SuppressTosNotice)
        {
            return true;
        }

        TosNoticeResult result = await _showNotice(cancellationToken).ConfigureAwait(true);
        if (result == TosNoticeResult.ContinueAndSuppress)
        {
            await _settings.UpdateAsync(s => s with { SuppressTosNotice = true }, cancellationToken)
                .ConfigureAwait(true);
        }

        return result != TosNoticeResult.Cancel;
    }
}
