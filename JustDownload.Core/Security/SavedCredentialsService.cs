using JustDownload.Core.Abstractions;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Logging;
using JustDownload.Core.Settings;

namespace JustDownload.Core.Security;

/// <summary>What a saved credential protects (TASK-126), so the UI can describe it without revealing the secret.</summary>
public enum SavedCredentialKind
{
    /// <summary>The global proxy auth password (settings).</summary>
    GlobalProxyPassword = 0,

    /// <summary>A per-download proxy override password (TASK-153).</summary>
    DownloadProxyPassword = 1,

    /// <summary>Per-download captured browser cookies (TASK-091).</summary>
    DownloadCookies = 2,
}

/// <summary>
/// A saved credential the app holds in the OS keychain (TASK-126). Carries only non-secret metadata — a kind,
/// a human description, and the owning download id (when applicable) — never the secret value itself (§5).
/// </summary>
public sealed record SavedCredential(SavedCredentialKind Kind, string Description, long? DownloadId);

/// <summary>
/// Lists and removes the credentials the app has saved to the OS keychain (TASK-126), so the Authentication
/// settings can show what's stored and let the user revoke it. The app only knows the references it persisted
/// (the global proxy password and per-download cookie/proxy secrets), so this enumerates those — it never
/// reads the secret values, and removal deletes the keychain entry and clears the owning reference.
/// </summary>
public interface ISavedCredentialsService
{
    Task<IReadOnlyList<SavedCredential>> ListAsync(CancellationToken cancellationToken = default);

    Task RemoveAsync(SavedCredential credential, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes cookie secrets that can no longer do their job, returning how many went. Two rules: a download
    /// that has already completed can never resend them (the engine drops these as each download finishes —
    /// this catches records saved before it did), and cookies captured more than
    /// <see cref="SavedCredentialsService.CookieMaxAge"/> ago are past any plausible expiry whatever state
    /// their download is in. Idempotent — a second run over the same data finds nothing.
    /// </summary>
    Task<int> PurgeStaleDownloadCookiesAsync(CancellationToken cancellationToken = default);
}

internal sealed class SavedCredentialsService : ISavedCredentialsService
{
    /// <summary>
    /// How long a captured cookie is worth keeping. Cookies are captured once, at enqueue, and never
    /// refreshed — so a record's age *is* its cookies' age, whether or not the download ever ran. Past this
    /// they are almost certainly expired server-side and are only a liability in the keychain (§5). Thirty
    /// days sits beyond the usual persistent-cookie lifetime without being so long as to be meaningless.
    /// </summary>
    internal static readonly TimeSpan CookieMaxAge = TimeSpan.FromDays(30);

    private readonly ISettingsService _settings;
    private readonly IDownloadRepository _downloads;
    private readonly ISecretStore _secrets;
    private readonly IClock _clock;

    public SavedCredentialsService(
        ISettingsService settings, IDownloadRepository downloads, ISecretStore secrets, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(clock);
        _settings = settings;
        _downloads = downloads;
        _secrets = secrets;
        _clock = clock;
    }

    public async Task<IReadOnlyList<SavedCredential>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<SavedCredential>();

        AppSettings settings = _settings.Current;
        if (!string.IsNullOrEmpty(settings.ProxyPasswordSecretRef))
        {
            string host = string.IsNullOrWhiteSpace(settings.ProxyHost) ? "the proxy" : settings.ProxyHost!;
            result.Add(new SavedCredential(
                SavedCredentialKind.GlobalProxyPassword, $"Proxy password for {host}", DownloadId: null));
        }

        IReadOnlyList<Download> downloads = await _downloads.GetAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (Download download in downloads)
        {
            string label = Describe(download);
            if (!string.IsNullOrEmpty(download.ProxyPasswordSecretRef))
            {
                result.Add(new SavedCredential(
                    SavedCredentialKind.DownloadProxyPassword, $"Proxy password for {label}", download.Id));
            }

            if (!string.IsNullOrEmpty(download.CookieSecretRef))
            {
                result.Add(new SavedCredential(
                    SavedCredentialKind.DownloadCookies, $"Cookies for {label}", download.Id));
            }
        }

        return result;
    }

    /// <summary>
    /// Names a download in a way the user can act on: its file name plus the log-safe origin. The host alone
    /// is not enough to choose what to revoke — every download from one site reads identically, which is
    /// exactly what this panel showed before (user-reported: fifteen indistinguishable "github.com" rows).
    /// The file name is already non-secret (it is the list's primary label); the URL still goes through
    /// <see cref="SafeLogUrl"/>, so no query string, token or userinfo reaches the description (§5).
    /// </summary>
    private static string Describe(Download download) =>
        string.IsNullOrWhiteSpace(download.Filename)
            ? $"download {SafeLogUrl.Of(download.Url)}"
            : $"{download.Filename} — {SafeLogUrl.Of(download.Url)}";

    public async Task<int> PurgeStaleDownloadCookiesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Download> downloads = await _downloads.GetAllAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset capturedBefore = _clock.UtcNow - CookieMaxAge;
        int purged = 0;
        foreach (Download download in downloads)
        {
            if (download.CookieSecretRef is not { Length: > 0 } || !IsStale(download, capturedBefore))
            {
                continue;
            }

            await DeleteSecretAsync(download.CookieSecretRef, cancellationToken).ConfigureAwait(false);
            await _downloads.UpdateAsync(download with { CookieSecretRef = null }, cancellationToken)
                .ConfigureAwait(false);
            purged++;
        }

        return purged;
    }

    private static bool IsStale(Download download, DateTimeOffset capturedBefore) =>
        download.Status == DownloadStatusCodes.Completed || download.CreatedAt < capturedBefore;

    public async Task RemoveAsync(SavedCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        switch (credential.Kind)
        {
            case SavedCredentialKind.GlobalProxyPassword:
                await DeleteSecretAsync(_settings.Current.ProxyPasswordSecretRef, cancellationToken).ConfigureAwait(false);
                await _settings.UpdateAsync(s => s with { ProxyPasswordSecretRef = null }, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case SavedCredentialKind.DownloadProxyPassword when credential.DownloadId is { } proxyId:
                await RemoveDownloadSecretAsync(
                    proxyId, d => d.ProxyPasswordSecretRef, d => d with { ProxyPasswordSecretRef = null }, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case SavedCredentialKind.DownloadCookies when credential.DownloadId is { } cookieId:
                await RemoveDownloadSecretAsync(
                    cookieId, d => d.CookieSecretRef, d => d with { CookieSecretRef = null }, cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                break;
        }
    }

    private async Task RemoveDownloadSecretAsync(
        long downloadId,
        Func<Download, string?> selectRef,
        Func<Download, Download> clearRef,
        CancellationToken cancellationToken)
    {
        Download? download = await _downloads.GetAsync(downloadId, cancellationToken).ConfigureAwait(false);
        if (download is null)
        {
            return;
        }

        await DeleteSecretAsync(selectRef(download), cancellationToken).ConfigureAwait(false);
        await _downloads.UpdateAsync(clearRef(download), cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteSecretAsync(string? secretRef, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(secretRef))
        {
            await _secrets.DeleteAsync(secretRef, cancellationToken).ConfigureAwait(false);
        }
    }
}
