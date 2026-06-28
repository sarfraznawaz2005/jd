using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.NativeMessaging;

/// <summary>
/// Ensures the desktop app is running so a handed-off link is acted on (TASK-070, US-11 AC5). When the
/// native host receives a link and the app is not already running, this launches it; if it is running,
/// launching is skipped (the app picks up the queued link). The running check and launch are injected so the
/// decision is unit-testable without a real process or mutex.
/// </summary>
public interface IAppLauncher
{
    /// <summary>Launches the desktop app if it is not already running.</summary>
    Task EnsureRunningAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IAppLauncher"/> (TASK-070). Detects a running app via its single-instance mutex and
/// launches the app executable (resolved next to the host) when absent. The probe and launch action are
/// constructor-injected so the launch-only-when-needed decision is unit-tested; the DI default wires the
/// real mutex probe and <see cref="Process"/> start.
/// </summary>
public sealed partial class AppLauncher : IAppLauncher
{
    /// <summary>The single-instance mutex name the app holds while running (matches the App coordinator).</summary>
    public const string RunningMutexName = "JustDownload.SingleInstance.mutex";

    private readonly Func<bool> _isRunning;
    private readonly Action _launch;
    private readonly ILogger<AppLauncher> _logger;

    /// <summary>Creates a launcher with explicit probe/launch delegates (used by tests).</summary>
    public AppLauncher(Func<bool> isRunning, Action launch, ILogger<AppLauncher> logger)
    {
        ArgumentNullException.ThrowIfNull(isRunning);
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(logger);
        _isRunning = isRunning;
        _launch = launch;
        _logger = logger;
    }

    /// <summary>Creates the production launcher (mutex probe + process start). The DI default.</summary>
    public AppLauncher(ILogger<AppLauncher> logger)
        : this(IsAppRunning, LaunchApp, logger)
    {
    }

    public Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning())
        {
            return Task.CompletedTask;
        }

        try
        {
            _launch();
            LogLaunched(_logger);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            LogLaunchFailed(_logger, ex);
        }

        return Task.CompletedTask;
    }

    private static bool IsAppRunning()
    {
        try
        {
            using Mutex mutex = Mutex.OpenExisting(RunningMutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // it exists but is owned by another session
        }
    }

    private static void LaunchApp()
    {
        string exe = ResolveAppExecutable();
        if (File.Exists(exe))
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
    }

    private static string ResolveAppExecutable()
    {
        string dir = AppContext.BaseDirectory;
        string name = OperatingSystem.IsWindows() ? "JustDownload.App.exe" : "JustDownload.App";
        return Path.Combine(dir, name);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Launched the desktop app for a handed-off link.")]
    private static partial void LogLaunched(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to launch the desktop app.")]
    private static partial void LogLaunchFailed(ILogger logger, Exception exception);
}
