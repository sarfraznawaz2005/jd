namespace JustDownload.Core.NativeMessaging;

/// <summary>
/// Checks whether the desktop app is currently running, via its single-instance mutex (TASK-070, TASK-185).
/// Shared by <see cref="AppLauncher"/> (its already-running-vs-launch decision) and the native host's ping
/// handler (TASK-185: "App connected" in the extension popup must mean the app itself is running, not just
/// that the native host process — a separate, short-lived process the browser can spawn on its own —
/// managed to answer a ping. Before this fix, ping/pong never checked this at all, so the popup showed
/// "connected" even with the app fully closed).
/// </summary>
public interface IAppRunningProbe
{
    /// <summary>Whether the desktop app is currently running.</summary>
    bool IsRunning();
}

/// <summary>Default <see cref="IAppRunningProbe"/>: opens the app's single-instance mutex by name.</summary>
public sealed class AppRunningProbe : IAppRunningProbe
{
    public bool IsRunning()
    {
        try
        {
            using Mutex mutex = Mutex.OpenExisting(AppLauncher.RunningMutexName);
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
}
