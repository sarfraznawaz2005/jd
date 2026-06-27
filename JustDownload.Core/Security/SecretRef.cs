namespace JustDownload.Core.Security;

/// <summary>
/// Generates the opaque references that point into the OS secret vault. A reference carries no
/// information about the secret it names; it is a random, filesystem- and keychain-account-safe
/// token (hex, no separators) so the same value works as a vault key on every platform.
/// </summary>
internal static class SecretRef
{
    /// <summary>Creates a fresh, unique secret reference.</summary>
    public static string New() => Guid.NewGuid().ToString("N");
}
