namespace JustDownload.Core;

/// <summary>
/// Detects "portable mode" (TASK-138): when a marker file (<see cref="MarkerFileName"/>) sits next to the
/// executable, the app keeps all state in a <see cref="DataFolderName"/> folder beside the executable and
/// skips OS-integration writes (the registry — autostart and native-host registration), so it runs
/// self-contained from a USB stick and leaves no trace on the host. Pure path logic; no I/O beyond a single
/// existence check, and the base directory is a parameter so it is fully testable.
/// </summary>
public static class PortableMode
{
    /// <summary>The marker file whose presence beside the executable enables portable mode.</summary>
    public const string MarkerFileName = "portable.dat";

    /// <summary>The folder (beside the executable) that holds all portable state.</summary>
    public const string DataFolderName = "Data";

    /// <summary>Whether portable mode is enabled for an app whose executable lives in <paramref name="baseDirectory"/>.</summary>
    public static bool IsPortable(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);
        return File.Exists(Path.Combine(baseDirectory, MarkerFileName));
    }

    /// <summary>The portable state directory beside an executable in <paramref name="baseDirectory"/>.</summary>
    public static string DataDirectory(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);
        return Path.Combine(baseDirectory, DataFolderName);
    }

    /// <summary>The running app's base directory (where the executable and any marker file live).</summary>
    public static string BaseDirectory => AppContext.BaseDirectory;

    /// <summary>Whether the running app is in portable mode.</summary>
    public static bool IsPortable() => IsPortable(BaseDirectory);
}
