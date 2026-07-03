using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.NativeMessaging;

/// <summary>
/// Ensures the desktop app is running so a handed-off link is acted on (TASK-070, US-11 AC5). When the
/// native host receives a link and the app is not already running, this launches it; if it is running, it
/// signals the running instance over the single-instance pipe (TASK-182) so it re-checks the extension
/// inbox immediately instead of only at the next full restart — before this, "if it is running... the app
/// picks up the queued link" was only a doc-comment aspiration: nothing actually notified a running
/// instance, so a hand-off silently sat in the inbox file until the app was next closed and reopened (a
/// real, user-reported bug: "Download with JustDownload" appeared to do nothing while the app was open).
/// The running check, launch, and notify are all injected so the decision is unit-testable without a real
/// process, mutex, or pipe.
/// </summary>
public interface IAppLauncher
{
    /// <summary>Launches the desktop app if it is not already running; notifies it if it is.</summary>
    Task EnsureRunningAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IAppLauncher"/> (TASK-070, TASK-182). Detects a running app via its single-instance
/// mutex, launches the app executable (resolved next to the host) when absent, or signals it over the
/// shared single-instance pipe when present. The probe, launch, and notify actions are constructor-injected
/// so the launch-vs-notify decision is unit-tested; the DI default wires the real mutex probe, process
/// start, and named-pipe client.
/// </summary>
public sealed partial class AppLauncher : IAppLauncher
{
    /// <summary>The single-instance mutex name the app holds while running (matches the App coordinator).</summary>
    public const string RunningMutexName = "JustDownload.SingleInstance.mutex";

    private static readonly TimeSpan NotifyConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<bool> _isRunning;
    private readonly Action _launch;
    private readonly Func<CancellationToken, Task> _notifyRunning;
    private readonly ILogger<AppLauncher> _logger;

    /// <summary>Creates a launcher with explicit probe/launch/notify delegates (used by tests).</summary>
    public AppLauncher(
        Func<bool> isRunning, Action launch, Func<CancellationToken, Task> notifyRunning, ILogger<AppLauncher> logger)
    {
        ArgumentNullException.ThrowIfNull(isRunning);
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(notifyRunning);
        ArgumentNullException.ThrowIfNull(logger);
        _isRunning = isRunning;
        _launch = launch;
        _notifyRunning = notifyRunning;
        _logger = logger;
    }

    /// <summary>Creates the production launcher (mutex probe + process start + named-pipe notify). The DI default.</summary>
    public AppLauncher(ILogger<AppLauncher> logger)
        : this(new AppRunningProbe().IsRunning, LaunchApp, NotifyRunningInstanceAsync, logger)
    {
    }

    public async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning())
        {
            try
            {
                await _notifyRunning(cancellationToken).ConfigureAwait(false);
                LogNotified(_logger);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            {
                // Best-effort: the link is already durably queued in the inbox regardless (TASK-070 AC1),
                // so a failed notify just falls back to today's behavior — picked up on the next restart.
                LogNotifyFailed(_logger, ex);
            }

            return;
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

    /// <summary>Connects to the running instance's single-instance pipe (the same one App's
    /// SingleInstanceCoordinator listens on for forwarded-launch arguments) and sends the drain-inbox
    /// signal instead of a URL (TASK-182).</summary>
    private static async Task NotifyRunningInstanceAsync(CancellationToken cancellationToken)
    {
        await using var client = new NamedPipeClientStream(
            ".", SingleInstancePipeName.Resolve(), PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync((int)NotifyConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);

        byte[] payload = Encoding.UTF8.GetBytes(SingleInstancePipeName.DrainInboxSignal);
        await client.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await client.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Launched the desktop app for a handed-off link.")]
    private static partial void LogLaunched(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to launch the desktop app.")]
    private static partial void LogLaunchFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Notified the running app of a handed-off link.")]
    private static partial void LogNotified(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Failed to notify the running app; it will pick up the link on next start.")]
    private static partial void LogNotifyFailed(ILogger logger, Exception exception);
}
