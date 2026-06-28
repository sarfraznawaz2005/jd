using JustDownload.Core.Abstractions;

namespace JustDownload.Core.Data;

/// <summary>
/// Default <see cref="IDatabasePathProvider"/>. Resolves the database under the per-OS
/// application-data directory (<see cref="Environment.SpecialFolder.ApplicationData"/> —
/// <c>%APPDATA%</c> on Windows, <c>$XDG_CONFIG_HOME</c>/<c>~/.config</c> on Linux/macOS) in a
/// "JustDownload" subfolder, keeping the engine free of any hard-coded path.
/// </summary>
internal sealed class DatabasePathProvider : IDatabasePathProvider
{
    /// <summary>The database file name within the application-data subfolder.</summary>
    internal const string FileName = "justdownload.db";

    public DatabasePathProvider(IAppInfoProvider appInfo)
    {
        ArgumentNullException.ThrowIfNull(appInfo);

        // Honors JUSTDOWNLOAD_DATA_DIR; otherwise the per-OS app-data folder under the app name. The
        // directory is created lazily by the connection factory before the first open.
        DatabaseDirectory = AppDataPaths.Directory(appInfo);
        DatabasePath = Path.Combine(DatabaseDirectory, FileName);
    }

    public string DatabaseDirectory { get; }

    public string DatabasePath { get; }
}
