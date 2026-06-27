using System.Runtime.Versioning;
using JustDownload.Core.Abstractions;

namespace JustDownload.Core.Security;

/// <summary>
/// macOS <see cref="ISecretStore"/> backed by the login Keychain via the system <c>security</c>
/// tool (a separate child process — no native linkage, so no license entanglement; CLAUDE.md §4).
/// Each secret is a generic password whose <c>service</c> is the product name and whose
/// <c>account</c> is the opaque secret reference.
/// </summary>
/// <remarks>
/// Implemented for parity and pending on-OS verification (this build is validated on Windows). Known
/// limitation: <c>security add-generic-password</c> has no stdin password input, so the value is
/// passed via <c>-w</c> on the argument vector (visible to <c>ps</c> for the call's lifetime). A
/// later hardening pass can switch to a P/Invoke of <c>SecItemAdd</c> to remove that exposure.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class MacOsKeychainSecretStore : ISecretStore
{
    private const string SecurityTool = "/usr/bin/security";

    // 'security' exits 44 (errSecItemNotFound) when the requested item is absent.
    private const int ItemNotFound = 44;

    private readonly string _service;

    public MacOsKeychainSecretStore(IAppInfoProvider appInfo)
    {
        ArgumentNullException.ThrowIfNull(appInfo);
        _service = appInfo.Name;
    }

    public async Task<string> StoreAsync(string secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);

        string secretRef = SecretRef.New();
        CommandLineRunner.Result result = await CommandLineRunner.RunAsync(
            SecurityTool,
            ["add-generic-password", "-a", secretRef, "-s", _service, "-w", secret, "-U"],
            standardInput: null,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new SecretStoreException(
                $"Failed to store secret in the macOS Keychain (security exit {result.ExitCode}).");
        }

        return secretRef;
    }

    public async Task<string?> RetrieveAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(secretRef);

        CommandLineRunner.Result result = await CommandLineRunner.RunAsync(
            SecurityTool,
            ["find-generic-password", "-a", secretRef, "-s", _service, "-w"],
            standardInput: null,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode == ItemNotFound)
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            throw new SecretStoreException(
                $"Failed to read secret from the macOS Keychain (security exit {result.ExitCode}).");
        }

        // 'security -w' prints the raw password followed by a single newline.
        return TrimSingleTrailingNewline(result.StandardOutput);
    }

    public async Task<bool> DeleteAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(secretRef);

        CommandLineRunner.Result result = await CommandLineRunner.RunAsync(
            SecurityTool,
            ["delete-generic-password", "-a", secretRef, "-s", _service],
            standardInput: null,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode == ItemNotFound)
        {
            return false;
        }

        if (result.ExitCode != 0)
        {
            throw new SecretStoreException(
                $"Failed to delete secret from the macOS Keychain (security exit {result.ExitCode}).");
        }

        return true;
    }

    private static string TrimSingleTrailingNewline(string value) =>
        value.EndsWith('\n') ? value[..^1] : value;
}
