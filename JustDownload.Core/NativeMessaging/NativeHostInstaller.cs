using JustDownload.Core.Abstractions;
using JustDownload.Core.NativeMessaging.Registration;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.NativeMessaging;

/// <summary>
/// Registers this install's native-messaging host so browsers can find and launch it (TASK-065/089, US-11).
/// The desktop app calls <see cref="Install"/> on startup; it builds the <see cref="NativeHostRegistration"/>
/// from <see cref="NativeHostIdentity"/> and the host executable that ships next to the app, then writes the
/// per-browser manifests (and Windows registry entries) via the <see cref="INativeHostRegistrar"/>.
/// <para>
/// If the host executable is not present next to the app (e.g. a dev run where only the app was built), it
/// skips registration rather than pointing browsers at a missing binary — registration only takes effect
/// once the host is deployed alongside the app.
/// </para>
/// </summary>
public sealed partial class NativeHostInstaller
{
    private readonly INativeHostRegistrar _registrar;
    private readonly IAppInfoProvider _appInfo;
    private readonly Func<string> _hostExecutablePath;
    private readonly Func<string, bool> _fileExists;
    private readonly ILogger<NativeHostInstaller> _logger;

    /// <summary>The DI default: resolves the host executable next to the app and uses the real file system.</summary>
    public NativeHostInstaller(
        INativeHostRegistrar registrar, IAppInfoProvider appInfo, ILogger<NativeHostInstaller> logger)
        : this(registrar, appInfo, DefaultHostExecutablePath, File.Exists, logger)
    {
    }

    /// <summary>Creates an installer with explicit host-path/existence probes (used by tests).</summary>
    public NativeHostInstaller(
        INativeHostRegistrar registrar,
        IAppInfoProvider appInfo,
        Func<string> hostExecutablePath,
        Func<string, bool> fileExists,
        ILogger<NativeHostInstaller> logger)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        ArgumentNullException.ThrowIfNull(appInfo);
        ArgumentNullException.ThrowIfNull(hostExecutablePath);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(logger);
        _registrar = registrar;
        _appInfo = appInfo;
        _hostExecutablePath = hostExecutablePath;
        _fileExists = fileExists;
        _logger = logger;
    }

    /// <summary>
    /// Registers the native host for every supported browser when the host executable is present.
    /// Returns <see langword="true"/> if registration ran, <see langword="false"/> if it was skipped because
    /// the host executable was not found.
    /// </summary>
    public bool Install()
    {
        string exe = _hostExecutablePath();
        if (!_fileExists(exe))
        {
            LogHostMissing(_logger, exe);
            return false;
        }

        try
        {
            _registrar.Register(new NativeHostRegistration
            {
                Name = NativeHostIdentity.HostName,
                Description = $"{_appInfo.Name} Native Messaging Host",
                ExecutablePath = exe,
                AllowedExtensionIds = NativeHostIdentity.FirefoxExtensionIds,
                AllowedOrigins = NativeHostIdentity.ChromiumOrigins,
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Registration is best-effort at startup; a file/registry failure is logged, not fatal.
            LogRegistrationFailed(_logger, ex);
            return false;
        }
    }

    /// <summary>Removes this install's native-host manifests and registry entries (uninstall path).</summary>
    public void Uninstall() => _registrar.Unregister(NativeHostIdentity.HostName);

    private static string DefaultHostExecutablePath() => Path.Combine(
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "JustDownload.NativeHost.exe" : "JustDownload.NativeHost");

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Native host executable not found at {Path}; skipping browser registration.")]
    private static partial void LogHostMissing(ILogger logger, string path);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Native host browser registration failed.")]
    private static partial void LogRegistrationFailed(ILogger logger, Exception exception);
}
