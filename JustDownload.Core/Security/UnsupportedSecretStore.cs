namespace JustDownload.Core.Security;

/// <summary>
/// Fallback <see cref="ISecretStore"/> registered when the host OS has no supported keychain
/// backend. Storing a credential without a real OS vault would mean inventing a plaintext store —
/// which CLAUDE.md §5 forbids — so every operation fails loudly instead of degrading silently
/// (CLAUDE.md §1 "no silent failures").
/// </summary>
internal sealed class UnsupportedSecretStore : ISecretStore
{
    private static SecretStoreException NotSupported() => new(
        "No OS secret vault is available on this platform; credentials cannot be stored securely.");

    public Task<string> StoreAsync(string secret, CancellationToken cancellationToken = default) =>
        throw NotSupported();

    public Task<string?> RetrieveAsync(string secretRef, CancellationToken cancellationToken = default) =>
        throw NotSupported();

    public Task<bool> DeleteAsync(string secretRef, CancellationToken cancellationToken = default) =>
        throw NotSupported();
}
