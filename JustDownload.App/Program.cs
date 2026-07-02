using Avalonia;
using JustDownload.App.Services;

namespace JustDownload.App;

/// <summary>Process entry point that bootstraps the Avalonia application.</summary>
internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized yet.
    [STAThread]
    public static int Main(string[] args)
    {
        // The Windows installer's uninstall custom action (TASK-076): remove native-host registrations
        // before Avalonia (or the single-instance coordinator) is touched at all, then exit.
        if (args.Length > 0 && args[0] == UninstallCleanup.Argument)
        {
            return UninstallCleanup.Run();
        }

        // Single-instance (TASK-061 AC2): a second launch forwards its URL/arguments to the running
        // instance and exits, so links always open in the one window.
        var coordinator = new SingleInstanceCoordinator();
        if (!coordinator.TryClaimOwnership())
        {
            try
            {
                coordinator.ForwardArgumentsAsync(args).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                // The owner went away between the check and the send — fall through and start normally.
            }

            coordinator.Dispose();
            return 0;
        }

        App.InstanceCoordinator = coordinator;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            coordinator.Dispose();
        }

        return 0;
    }

    /// <summary>Avalonia configuration, shared by the runtime entry point and the visual designer.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
