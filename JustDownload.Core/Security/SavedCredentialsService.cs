using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
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
}

internal sealed class SavedCredentialsService : ISavedCredentialsService
{
    private readonly ISettingsService _settings;
    private readonly IDownloadRepository _downloads;
    private readonly ISecretStore _secrets;

    public SavedCredentialsService(
        ISettingsService settings, IDownloadRepository downloads, ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(secrets);
        _settings = settings;
        _downloads = downloads;
        _secrets = secrets;
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
            string label = SafeLogUrl.Of(download.Url);
            if (!string.IsNullOrEmpty(download.ProxyPasswordSecretRef))
            {
                result.Add(new SavedCredential(
                    SavedCredentialKind.DownloadProxyPassword, $"Proxy password for download {label}", download.Id));
            }

            if (!string.IsNullOrEmpty(download.CookieSecretRef))
            {
                result.Add(new SavedCredential(
                    SavedCredentialKind.DownloadCookies, $"Cookies for download {label}", download.Id));
            }
        }

        return result;
    }

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
