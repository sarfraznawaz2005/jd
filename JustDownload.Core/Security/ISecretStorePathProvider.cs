namespace JustDownload.Core.Security;

/// <summary>
/// Resolves the on-disk directory for the Windows DPAPI secret vault. DPAPI encrypts but does not
/// store, so the per-secret ciphertext blobs need a home outside SQLite; this seam keeps that
/// location in one mockable place (tests point it at a temp directory) and away from any hard-coded
/// path. Unused by the macOS/Linux stores, which delegate storage to the OS keychain daemon.
/// </summary>
public interface ISecretStorePathProvider
{
    /// <summary>
    /// The directory that holds the DPAPI-protected secret blobs, under the per-OS application-data
    /// directory in the app's <c>secrets</c> subfolder. Created lazily on first write.
    /// </summary>
    string SecretsDirectory { get; }
}
