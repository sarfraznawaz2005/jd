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

    public static string Directory(IAppInfoProvider appInfo)
    {
        ArgumentNullException.ThrowIfNull(appInfo);

        string? overrideDir = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return overrideDir;
        }

        string appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        return Path.Combine(appData, appInfo.Name);
    }
}
