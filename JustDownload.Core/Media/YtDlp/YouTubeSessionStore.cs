using JustDownload.Core.Abstractions;
using JustDownload.Core.Security;
using JustDownload.Core.Settings;

namespace JustDownload.Core.Media.YtDlp;

/// <summary>Default <see cref="IYouTubeSessionStore"/>. See the interface for the design.</summary>
internal sealed class YouTubeSessionStore : IYouTubeSessionStore
{
    private readonly ISettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly string _cookieFilePath;

    public YouTubeSessionStore(ISettingsService settings, ISecretStore secrets, IAppInfoProvider appInfo)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(appInfo);
        _settings = settings;
        _secrets = secrets;

        // A stable, deterministic path (not a fresh temp file per store) so a restart can detect and
        // repair it going missing (EnsureMaterializedAsync) without needing to remember a generated name.
        _cookieFilePath = Path.Combine(Path.GetTempPath(), appInfo.Name, "youtube-session-cookies.txt");
    }

    public bool HasSession => !string.IsNullOrEmpty(_settings.Current.YouTubeSessionSecretRef);

    public async Task StoreAsync(
        IReadOnlyList<NetscapeCookieRecord> cookies, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cookies);

        // Replacing an existing session: drop the old vault entry first so signing in again never leaves
        // an orphaned secret behind.
        string? previousRef = _settings.Current.YouTubeSessionSecretRef;
        if (!string.IsNullOrEmpty(previousRef))
        {
            await _secrets.DeleteAsync(previousRef, cancellationToken).ConfigureAwait(false);
        }

        string content = NetscapeCookieFileWriter.Write(cookies);
        string secretRef = await _secrets.StoreAsync(content, cancellationToken).ConfigureAwait(false);

        await WriteCookieFileAsync(content, cancellationToken).ConfigureAwait(false);

        await _settings
            .UpdateAsync(
                s => s with { YouTubeSessionSecretRef = secretRef, YtDlpCookieFilePath = _cookieFilePath },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        string? secretRef = _settings.Current.YouTubeSessionSecretRef;
        if (!string.IsNullOrEmpty(secretRef))
        {
            await _secrets.DeleteAsync(secretRef, cancellationToken).ConfigureAwait(false);
        }

        DeleteCookieFileIfExists();

        await _settings
            .UpdateAsync(s => s with { YouTubeSessionSecretRef = null, YtDlpCookieFilePath = null }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task EnsureMaterializedAsync(CancellationToken cancellationToken = default)
    {
        string? secretRef = _settings.Current.YouTubeSessionSecretRef;
        if (string.IsNullOrEmpty(secretRef) || File.Exists(_cookieFilePath))
        {
            return;
        }

        string? content = await _secrets.RetrieveAsync(secretRef, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            // The vault entry is gone (e.g. the user cleared it outside the app) — the dangling reference
            // would otherwise keep pointing YtDlpCookieFilePath at a file that can never come back.
            await ClearAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteCookieFileAsync(content, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteCookieFileAsync(string content, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_cookieFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(_cookieFilePath, content, cancellationToken).ConfigureAwait(false);
    }

    private void DeleteCookieFileIfExists()
    {
        if (File.Exists(_cookieFilePath))
        {
            File.Delete(_cookieFilePath);
        }
    }
}
