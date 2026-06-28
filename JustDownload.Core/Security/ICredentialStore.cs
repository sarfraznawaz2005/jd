using JustDownload.Core.Transport.Auth;

namespace JustDownload.Core.Security;

/// <summary>
/// A credential persisted for reuse (TASK-035 AC1): the non-secret <see cref="Username"/>/<see cref="Domain"/>
/// plus an opaque <see cref="SecretRef"/> that points at the password in the OS keychain. Only this record
/// is safe to store in SQLite — the password itself never leaves the keychain.
/// </summary>
/// <param name="Username">The account user name.</param>
/// <param name="Domain">The NTLM/Negotiate domain, or <see langword="null"/>.</param>
/// <param name="SecretRef">The opaque keychain reference for the password.</param>
public sealed record StoredCredential(string Username, string? Domain, string SecretRef);

/// <summary>
/// Persists download/proxy credentials with the password held only in the OS keychain (TASK-035 AC1,
/// CLAUDE.md §5 "secrets at rest"). <see cref="SaveAsync"/> writes the password to <see cref="ISecretStore"/>
/// and returns a <see cref="StoredCredential"/> whose <see cref="StoredCredential.SecretRef"/> is the only
/// thing safe to put in the database; <see cref="LoadAsync"/> resolves the password back from the keychain.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Stores the password in the keychain and returns the persistable reference + username/domain.</summary>
    Task<StoredCredential> SaveAsync(NetworkCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves <paramref name="stored"/> back into usable credentials, or <see langword="null"/> if the
    /// keychain entry is gone.
    /// </summary>
    Task<NetworkCredentials?> LoadAsync(StoredCredential stored, CancellationToken cancellationToken = default);
}
