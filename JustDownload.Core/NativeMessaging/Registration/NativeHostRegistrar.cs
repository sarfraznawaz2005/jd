using Microsoft.Extensions.Logging;

namespace JustDownload.Core.NativeMessaging.Registration;

/// <summary>
/// Registers and unregisters the native-messaging host manifests for Chrome, Edge, and Firefox (TASK-065,
/// US-11). On install it writes each browser's manifest to its per-OS location (AC0/AC1) and, on Windows,
/// points the registry at it; on uninstall it removes both (AC2). Idempotent — re-registering overwrites and
/// unregistering missing entries is a no-op.
/// </summary>
public interface INativeHostRegistrar
{
    /// <summary>The browsers this registrar manages.</summary>
    IReadOnlyList<NativeMessagingBrowser> Browsers { get; }

    /// <summary>Writes the manifest for <paramref name="registration"/> to every browser's location.</summary>
    void Register(NativeHostRegistration registration);

    /// <summary>Removes the manifests (and registry entries) for the given host name from every browser.</summary>
    void Unregister(string hostName);

    /// <summary>The manifest file path that would be used for <paramref name="browser"/>.</summary>
    string ManifestPath(NativeMessagingBrowser browser, string hostName);
}

/// <summary>Default <see cref="INativeHostRegistrar"/> over <see cref="INativeHostManifestLocations"/> + the registry writer.</summary>
public sealed partial class NativeHostRegistrar : INativeHostRegistrar
{
    private static readonly NativeMessagingBrowser[] AllBrowsers =
        [NativeMessagingBrowser.Chrome, NativeMessagingBrowser.Edge, NativeMessagingBrowser.Firefox];

    private readonly INativeHostManifestLocations _locations;
    private readonly INativeHostRegistryWriter _registry;
    private readonly ILogger<NativeHostRegistrar> _logger;

    public NativeHostRegistrar(
        INativeHostManifestLocations locations,
        INativeHostRegistryWriter registry,
        ILogger<NativeHostRegistrar> logger)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        _locations = locations;
        _registry = registry;
        _logger = logger;
    }

    public IReadOnlyList<NativeMessagingBrowser> Browsers => AllBrowsers;

    public string ManifestPath(NativeMessagingBrowser browser, string hostName) =>
        _locations.ManifestPath(browser, hostName);

    public void Register(NativeHostRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        foreach (NativeMessagingBrowser browser in AllBrowsers)
        {
            string path = _locations.ManifestPath(browser, registration.Name);
            string json = NativeHostManifest.Build(browser, registration);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            _registry.SetHostPath(browser, registration.Name, path);
            LogRegistered(_logger, browser, path);
        }
    }

    public void Unregister(string hostName)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostName);

        foreach (NativeMessagingBrowser browser in AllBrowsers)
        {
            string path = _locations.ManifestPath(browser, hostName);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException ex)
            {
                LogUnregisterFailed(_logger, browser, ex);
            }

            _registry.Remove(browser, hostName);
            LogUnregistered(_logger, browser);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Registered native host for {Browser} at {Path}.")]
    private static partial void LogRegistered(ILogger logger, NativeMessagingBrowser browser, string path);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Unregistered native host for {Browser}.")]
    private static partial void LogUnregistered(ILogger logger, NativeMessagingBrowser browser);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Failed to remove the {Browser} native host manifest file.")]
    private static partial void LogUnregisterFailed(ILogger logger, NativeMessagingBrowser browser, Exception exception);
}
