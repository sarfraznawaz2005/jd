using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Lifecycle;

/// <summary>
/// Default <see cref="ISystemPowerController"/> (TASK-073): runs the platform's shutdown/sleep command as a
/// child process. This is reached only when the user opts into a post-queue power action, so it is never a
/// surprise. Unsupported platforms log and no-op rather than throwing.
/// </summary>
internal sealed partial class SystemPowerController : ISystemPowerController
{
    private readonly ILogger<SystemPowerController> _logger;

    public SystemPowerController(ILogger<SystemPowerController> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            Run("shutdown", "/s /t 0");
        }
        else if (OperatingSystem.IsLinux())
        {
            Run("systemctl", "poweroff");
        }
        else if (OperatingSystem.IsMacOS())
        {
            Run("osascript", "-e \"tell application \\\"System Events\\\" to shut down\"");
        }
        else
        {
            LogUnsupported(_logger, "shutdown");
        }

        return Task.CompletedTask;
    }

    public Task SleepAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            Run("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
        }
        else if (OperatingSystem.IsLinux())
        {
            Run("systemctl", "suspend");
        }
        else if (OperatingSystem.IsMacOS())
        {
            Run("pmset", "sleepnow");
        }
        else
        {
            LogUnsupported(_logger, "sleep");
        }

        return Task.CompletedTask;
    }

    private void Run(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LogPowerCommandFailed(_logger, fileName, ex);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Power action '{Action}' is not supported on this OS.")]
    private static partial void LogUnsupported(ILogger logger, string action);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Power command '{Command}' failed to start.")]
    private static partial void LogPowerCommandFailed(ILogger logger, string command, Exception exception);
}
