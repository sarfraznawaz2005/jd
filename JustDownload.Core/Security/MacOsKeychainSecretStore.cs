using System.Runtime.Versioning;
using JustDownload.Core.Abstractions;

namespace JustDownload.Core.Security;

/// <summary>
/// macOS <see cref="ISecretStore"/> backed by the login Keychain, reached via direct P/Invoke against
/// Apple's own Security.framework/CoreFoundation.framework (through <see cref="IMacKeychainInterop"/>)
/// — no child process is spawned, so the secret never appears on any process's command line or in
/// <c>ps</c> output (CLAUDE.md §5). Each secret is a generic password whose <c>service</c> is the
/// product name and whose <c>account</c> is the opaque secret reference.
/// </summary>
/// <remarks>
/// The logic in this class — secretRef generation and OSStatus-to-outcome mapping — has genuine
/// unit-test coverage against a fake <see cref="IMacKeychainInterop"/> (see
/// <c>MacOsKeychainSecretStoreTests</c>). The real interop implementation
/// (<see cref="MacKeychainInterop"/>) that actually calls Security.framework has not been executed on
/// real macOS hardware: this build/test run is Windows-only, and Apple's terms prohibit macOS
/// virtualization on non-Apple hardware, so there is currently no way to run it from here. On-OS
/// verification against a real Keychain is tracked as a separate follow-up (TASK-113's subtask).
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class MacOsKeychainSecretStore : ISecretStore
{
    // SecItemCopyMatching/SecItemDelete return errSecItemNotFound when nothing matches the query.
    private const int ItemNotFound = -25300;
    private const int Success = 0;

    private readonly string _service;
    private readonly IMacKeychainInterop _keychain;

    public MacOsKeychainSecretStore(IAppInfoProvider appInfo, IMacKeychainInterop keychain)
    {
        ArgumentNullException.ThrowIfNull(appInfo);
        ArgumentNullException.ThrowIfNull(keychain);
        _service = appInfo.Name;
        _keychain = keychain;
    }

    public Task<string> StoreAsync(string secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);

        string secretRef = SecretRef.New();
        int status = _keychain.Add(_service, secretRef, secret);

        if (status != Success)
        {
            throw new SecretStoreException(
                $"Failed to store secret in the macOS Keychain (OSStatus {status}).");
        }

        return Task.FromResult(secretRef);
    }

    public Task<string?> RetrieveAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(secretRef);

        (int status, string? secret) = _keychain.CopyMatching(_service, secretRef);

        if (status == ItemNotFound)
        {
            return Task.FromResult<string?>(null);
        }

        if (status != Success)
        {
            throw new SecretStoreException(
                $"Failed to read secret from the macOS Keychain (OSStatus {status}).");
        }

        return Task.FromResult(secret);
    }

    public Task<bool> DeleteAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(secretRef);

        int status = _keychain.Delete(_service, secretRef);

        if (status == ItemNotFound)
        {
            return Task.FromResult(false);
        }

        if (status != Success)
        {
            throw new SecretStoreException(
                $"Failed to delete secret from the macOS Keychain (OSStatus {status}).");
        }

        return Task.FromResult(true);
    }
}
