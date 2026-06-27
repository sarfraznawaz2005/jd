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

        // SpecialFolderOption.Create ensures the OS base directory exists; the secrets subfolder
        // itself is created lazily by the store before the first write.
        string appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create);

        SecretsDirectory = Path.Combine(appData, appInfo.Name, FolderName);
    }

    public string SecretsDirectory { get; }
}
