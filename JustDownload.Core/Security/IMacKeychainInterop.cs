namespace JustDownload.Core.Security;

/// <summary>
/// Seam over the native macOS Keychain calls so <see cref="MacOsKeychainSecretStore"/>'s
/// secret-reference generation and OSStatus-to-outcome mapping get real unit-test coverage without
/// the real Security.framework, which only exists on macOS. The production implementation
/// (<see cref="MacKeychainInterop"/>) P/Invokes Apple's own OS frameworks directly; nothing here
/// re-implements Keychain semantics.
/// </summary>
internal interface IMacKeychainInterop
{
    /// <summary>Adds a new generic-password item. Returns the raw <c>OSStatus</c> from <c>SecItemAdd</c>.</summary>
    int Add(string service, string account, string secret);

    /// <summary>
    /// Looks up a generic-password item's value. Returns the raw <c>OSStatus</c> from
    /// <c>SecItemCopyMatching</c> plus the recovered secret when the status is <c>errSecSuccess</c> (0).
    /// </summary>
    (int Status, string? Secret) CopyMatching(string service, string account);

    /// <summary>Deletes a generic-password item. Returns the raw <c>OSStatus</c> from <c>SecItemDelete</c>.</summary>
    int Delete(string service, string account);
}
