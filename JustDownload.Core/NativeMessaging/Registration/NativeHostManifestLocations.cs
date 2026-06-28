using JustDownload.Core.Abstractions;

namespace JustDownload.Core.NativeMessaging.Registration;

/// <summary>The OS a native-messaging host manifest is being placed for (TASK-065).</summary>
public enum HostOsPlatform
{
    /// <summary>Windows — the manifest file is stored in app data and the registry points at it.</summary>
    Windows,

    /// <summary>macOS — the manifest lives under <c>~/Library/Application Support/&lt;browser&gt;/NativeMessagingHosts</c>.</summary>
    MacOs,

    /// <summary>Linux — the manifest lives under the browser's per-user config directory.</summary>
    Linux,
}

/// <summary>Resolves the per-browser, per-OS native-messaging host manifest file path (TASK-065 AC1).</summary>
public interface INativeHostManifestLocations
{
    /// <summary>The OS these locations are computed for.</summary>
    HostOsPlatform Platform { get; }

    /// <summary>The absolute manifest file path for <paramref name="browser"/> and <paramref name="hostName"/>.</summary>
    string ManifestPath(NativeMessagingBrowser browser, string hostName);
}

/// <summary>
/// Default <see cref="INativeHostManifestLocations"/> (TASK-065). Encodes the documented per-OS locations:
/// on Windows the manifest is written under the app-data directory (the registry entry points to it), while
/// on macOS/Linux it goes straight into the browser's per-user <c>NativeMessagingHosts</c> directory. The OS
/// and base directories are constructor parameters so every platform's layout is unit-testable from any host.
/// </summary>
public sealed class NativeHostManifestLocations : INativeHostManifestLocations
{
    private readonly string _home;
    private readonly string _appData;
    private readonly string _appName;

    /// <summary>Creates locations for an explicit platform and base directories (used by tests).</summary>
    public NativeHostManifestLocations(HostOsPlatform platform, string home, string appData, string appName)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);
        ArgumentException.ThrowIfNullOrEmpty(appData);
        ArgumentException.ThrowIfNullOrEmpty(appName);
        Platform = platform;
        _home = home;
        _appData = appData;
        _appName = appName;
    }

    /// <summary>Creates locations for the current OS and user, named after the app (the DI default).</summary>
    public NativeHostManifestLocations(IAppInfoProvider appInfo)
        : this(
            DetectPlatform(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.Create),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
            (appInfo ?? throw new ArgumentNullException(nameof(appInfo))).Name)
    {
    }

    public HostOsPlatform Platform { get; }

    public string ManifestPath(NativeMessagingBrowser browser, string hostName)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostName);
        string file = hostName + ".json";

        return Platform switch
        {
            // The registry points at this file, so its location only needs to be stable per browser.
            HostOsPlatform.Windows =>
                Path.Combine(_appData, _appName, "NativeMessagingHosts", browser.ToString(), file),

            HostOsPlatform.MacOs => Path.Combine(
                _home, "Library", "Application Support", MacBrowserDir(browser), "NativeMessagingHosts", file),

            HostOsPlatform.Linux => Path.Combine(_home, LinuxBrowserDir(browser), file),

            _ => throw new ArgumentOutOfRangeException(nameof(browser)),
        };
    }

    private static string MacBrowserDir(NativeMessagingBrowser browser) => browser switch
    {
        NativeMessagingBrowser.Chrome => Path.Combine("Google", "Chrome"),
        NativeMessagingBrowser.Edge => "Microsoft Edge",
        NativeMessagingBrowser.Firefox => "Mozilla",
        _ => throw new ArgumentOutOfRangeException(nameof(browser)),
    };

    private static string LinuxBrowserDir(NativeMessagingBrowser browser) => browser switch
    {
        NativeMessagingBrowser.Chrome => Path.Combine(".config", "google-chrome", "NativeMessagingHosts"),
        NativeMessagingBrowser.Edge => Path.Combine(".config", "microsoft-edge", "NativeMessagingHosts"),
        NativeMessagingBrowser.Firefox => Path.Combine(".mozilla", "native-messaging-hosts"),
        _ => throw new ArgumentOutOfRangeException(nameof(browser)),
    };

    private static HostOsPlatform DetectPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return HostOsPlatform.Windows;
        }

        return OperatingSystem.IsMacOS() ? HostOsPlatform.MacOs : HostOsPlatform.Linux;
    }
}
