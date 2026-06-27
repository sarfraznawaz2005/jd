using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace JustDownload.Core.Security;

/// <summary>
/// Windows <see cref="ISecretStore"/> backed by DPAPI
/// (<see cref="ProtectedData"/>, <see cref="DataProtectionScope.CurrentUser"/>). DPAPI encrypts a
/// blob to the current user account but does not store it, so each secret is written as a
/// per-reference, DPAPI-protected file under the vault directory — the ciphertext, never plaintext,
/// is what touches disk (CLAUDE.md §5). Decryption succeeds only for the same Windows user, which is
/// the OS-keychain-equivalent guarantee on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDpapiSecretStore : ISecretStore
{
    // App-specific entropy mixed into every blob so a stray file cannot be unprotected outside this
    // application's context. It is not itself a secret (CLAUDE.md §5 forbids storing keys in code),
    // just a domain separator strengthening the per-user binding.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("JustDownload.SecretStore.v1");

    private const string FileExtension = ".secret";

    private readonly ISecretStorePathProvider _paths;

    public WindowsDpapiSecretStore(ISecretStorePathProvider paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    public async Task<string> StoreAsync(string secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);

        string secretRef = SecretRef.New();

        Directory.CreateDirectory(_paths.SecretsDirectory);
        byte[] cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);

        // Write base64 so the file is plainly non-plaintext and easy to inspect/transport.
        await File.WriteAllTextAsync(PathFor(secretRef), Convert.ToBase64String(cipher), cancellationToken)
            .ConfigureAwait(false);

        return secretRef;
    }

    public async Task<string?> RetrieveAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(secretRef);

        string path = PathFor(secretRef);
        if (!File.Exists(path))
        {
            return null;
        }

        string base64 = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        byte[] plain = ProtectedData.Unprotect(
            Convert.FromBase64String(base64), Entropy, DataProtectionScope.CurrentUser);

        return Encoding.UTF8.GetString(plain);
    }

    public Task<bool> DeleteAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(secretRef);

        string path = PathFor(secretRef);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    private string PathFor(string secretRef)
    {
        // secretRef is a hex GUID (SecretRef.New); reject anything else so a caller can never craft
        // a reference that escapes the vault directory (path traversal guard).
        if (!IsSafeReference(secretRef))
        {
            throw new ArgumentException(
                "Secret reference is not a valid vault key.", nameof(secretRef));
        }

        return Path.Combine(_paths.SecretsDirectory, secretRef + FileExtension);
    }

    private static bool IsSafeReference(string secretRef)
    {
        foreach (char c in secretRef)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return secretRef.Length > 0;
    }
}
