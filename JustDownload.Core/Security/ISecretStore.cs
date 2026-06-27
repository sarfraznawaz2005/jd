namespace JustDownload.Core.Security;

/// <summary>
/// Stores and retrieves sensitive values (HTTP/proxy passwords, bearer/OAuth tokens) in the
/// operating system's secret vault — DPAPI on Windows, the login Keychain on macOS, and the
/// Secret Service / libsecret on Linux (PRD §4.6, CLAUDE.md §5 "secrets at rest").
/// <para>
/// The contract that makes the privacy guarantee enforceable: the plaintext secret <b>never</b>
/// touches SQLite or logs. Callers persist only the opaque <c>secret_ref</c> returned by
/// <see cref="StoreAsync"/> — that pointer is what lands in the <c>auth.secret_ref</c> /
/// <c>proxies.secret_ref</c> columns (PRD §4.4) — and resolve the real value on demand via
/// <see cref="RetrieveAsync"/>. A <c>secret_ref</c> on its own discloses nothing.
/// </para>
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Persists <paramref name="secret"/> in the OS vault under a freshly generated, opaque
    /// reference and returns that reference. The returned value is the <b>only</b> thing safe to
    /// write to the database; it is not the secret and cannot be reversed into it.
    /// </summary>
    /// <param name="secret">The plaintext secret to protect. Must not be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the store operation.</param>
    /// <returns>An opaque <c>secret_ref</c> to persist in the database.</returns>
    Task<string> StoreAsync(string secret, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the plaintext secret previously stored under <paramref name="secretRef"/>, or
    /// <see langword="null"/> if no secret is stored under that reference (e.g. it was deleted or
    /// the vault entry is missing).
    /// </summary>
    /// <param name="secretRef">A reference returned by <see cref="StoreAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the retrieve operation.</param>
    Task<string?> RetrieveAsync(string secretRef, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the secret stored under <paramref name="secretRef"/> from the OS vault.
    /// </summary>
    /// <param name="secretRef">A reference returned by <see cref="StoreAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the delete operation.</param>
    /// <returns><see langword="true"/> if an entry existed and was removed; otherwise <see langword="false"/>.</returns>
    Task<bool> DeleteAsync(string secretRef, CancellationToken cancellationToken = default);
}
