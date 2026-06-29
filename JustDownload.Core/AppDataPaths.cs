using JustDownload.Core.Abstractions;

namespace JustDownload.Core;

/// <summary>
/// Resolves the per-user application-data directory for engine state (the SQLite database, the extension
/// hand-off inbox, …). Defaults to the OS application-data folder under the app name, but honors the
/// <c>JUSTDOWNLOAD_DATA_DIR</c> environment override so a host/app instance can be pointed at a custom
/// location (used to isolate the host-process e2e, and the basis for a future portable mode).
/// </summary>
internal static class AppDataPaths
{
    internal const string OverrideEnvironmentVariable = "JUSTDOWNLOAD_DATA_DIR";

    public static string Directory(IAppInfoProvider appInfo) =>
        Directory(appInfo, PortableMode.BaseDirectory);

    internal static string Directory(IAppInfoProvider appInfo, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(appInfo);
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);

        // Precedence: explicit env override (test/host isolation) > portable mode (beside the exe) > per-OS app-data.
        string? overrideDir = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return overrideDir;
        }

        if (PortableMode.IsPortable(baseDirectory))
        {
            return PortableMode.DataDirectory(baseDirectory);
        }

        string appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        return Path.Combine(appData, appInfo.Name);
    }
}
