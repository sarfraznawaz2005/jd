using JustDownload.Core;
using JustDownload.Core.NativeMessaging;
using Microsoft.Extensions.DependencyInjection;

namespace JustDownload.App.Services;

/// <summary>
/// Headless native-host cleanup entry point, invoked as <c>JustDownload.App.exe --uninstall-cleanup</c> by the
/// Windows installer's uninstall custom action (TASK-076). The Windows MSI only deletes files/shortcuts on
/// uninstall — it has no idea the app also wrote native-messaging manifests and an HKCU registry key
/// (<see cref="JustDownload.Core.NativeMessaging.Registration.NativeHostRegistrar"/>), so without this hook
/// those entries would be orphaned and browsers would keep trying to launch a host executable that no longer
/// exists. Runs before Avalonia is touched at all — no window, no single-instance coordinator — so it is safe
/// to invoke from a silent (<c>/qn</c>) uninstall.
/// </summary>
public static class UninstallCleanup
{
    /// <summary>The command-line switch that selects this path instead of the normal GUI startup.</summary>
    public const string Argument = "--uninstall-cleanup";

    /// <summary>Builds the minimal Core DI graph (same composition root as the CLI/GUI, §6) and runs cleanup.</summary>
    public static int Run()
    {
        using ServiceProvider services = new ServiceCollection().AddJustDownloadCore().BuildServiceProvider();
        return Run(services);
    }

    /// <summary>Runs cleanup against an existing service provider (used by tests to substitute the installer).</summary>
    public static int Run(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        try
        {
            services.GetRequiredService<INativeHostInstaller>().Uninstall();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Best-effort, matching NativeHostInstaller.Install()'s own failure handling: surface it (stderr is
            // captured into the MSI log by the WixQuietExec custom action) but don't fail the uninstall over it.
            Console.Error.WriteLine($"Native host cleanup failed: {ex.Message}");
            return 1;
        }
    }
}
