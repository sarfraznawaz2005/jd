using JustDownload.Core.Transport.Auth;

namespace JustDownload.Core.Security;

/// <summary>
/// Default <see cref="ICredentialStore"/> (TASK-035 AC1). The password is written to and read from the OS
/// keychain via <see cref="ISecretStore"/>; the username and domain (not secret) travel in the returned
/// <see cref="StoredCredential"/>. The plaintext password is never returned to a caller that only persists
/// the <see cref="StoredCredential"/>, and never reaches SQLite or logs.
/// </summary>
internal sealed class CredentialStore : ICredentialStore
{
    private readonly ISecretStore _secrets;

    public CredentialStore(ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = secrets;
    }

    public async Task<StoredCredential> SaveAsync(
        NetworkCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        string secretRef = await _secrets.StoreAsync(credentials.Password, cancellationToken).ConfigureAwait(false);
        return new StoredCredential(credentials.Username, credentials.Domain, secretRef);
    }

    public async Task<NetworkCredentials?> LoadAsync(
        StoredCredential stored, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stored);
        string? password = await _secrets.RetrieveAsync(stored.SecretRef, cancellationToken).ConfigureAwait(false);
        return password is null ? null : new NetworkCredentials(stored.Username, password, stored.Domain);
    }
}
