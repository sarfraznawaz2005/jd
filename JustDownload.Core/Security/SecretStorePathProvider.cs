using JustDownload.Core.Abstractions;

namespace JustDownload.Core.Security;

/// <summary>
/// Default <see cref="ISecretStorePathProvider"/>. Resolves the secret vault under the per-OS
/// application-data directory (<c>%APPDATA%</c> on Windows) in a <c>JustDownload/secrets</c>
/// subfolder, mirroring <see cref="JustDownload.Core.Data.DatabasePathProvider"/> so persistence
/// lives in one place per user.
/// </summary>
internal sealed class SecretStorePathProvider : ISecretStorePathProvider
{
    /// <summary>The vault subfolder name within the application-data directory.</summary>
    internal const string FolderName = "secrets";

    public SecretStorePathProvider(IAppInfoProvider appInfo)
    {
        ArgumentNullException.ThrowIfNull(appInfo);

        // Route through the shared app-data resolver so the vault follows the env override and portable mode
        // (TASK-138) exactly like the database; the secrets subfolder is created lazily before the first write.
        SecretsDirectory = Path.Combine(AppDataPaths.Directory(appInfo), FolderName);
    }

    public string SecretsDirectory { get; }
}
