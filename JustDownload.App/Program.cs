using Avalonia;
using JustDownload.App.Services;

namespace JustDownload.App;

/// <summary>Process entry point that bootstraps the Avalonia application.</summary>
internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized yet.
    [STAThread]
    public static void Main(string[] args)
    {
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
            return;
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
    }

    /// <summary>Avalonia configuration, shared by the runtime entry point and the visual designer.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
