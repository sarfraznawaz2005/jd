using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.App.Views;
using JustDownload.Core;
using JustDownload.Core.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace JustDownload.App;

/// <summary>The Avalonia application root; wires up the main window on startup.</summary>
public partial class App : Application
{
    /// <summary>
    /// The application's service provider: Core's single composition root (§6) plus the App-layer services
    /// (theme, download actions, OS integration, view-models). ViewModels resolve their dependencies here.
    /// </summary>
    public IServiceProvider Services { get; } =
        new ServiceCollection()
            .AddJustDownloadCore()
            .AddSingleton<IThemeService, ThemeService>()
            .AddSingleton<IDownloadActions, DownloadActionsService>()
            .AddSingleton<IFileRevealer, FileRevealer>()
            .AddSingleton<IClipboardService>(_ => new ClipboardService(GetActiveClipboard))
            .AddSingleton<StatusSummaryViewModel>()
            .AddSingleton<DownloadsListViewModel>()
            .AddSingleton<MainWindowViewModel>()
            .BuildServiceProvider();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Capture and surface unhandled exceptions process-wide — no silent failures (§1).
        Services.GetRequiredService<IGlobalErrorHandler>().Install();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>(),
            };

            // Bring the schema up to date, then load the persisted downloads into the list — off the
            // initialisation path so the window paints immediately and never blocks on I/O (§6).
            Dispatcher.UIThread.Post(async () => await InitializeAndLoadAsync(), DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeAndLoadAsync()
    {
        await Services.InitializeJustDownloadCoreAsync().ConfigureAwait(true);
        await Services.GetRequiredService<DownloadsListViewModel>().LoadAsync().ConfigureAwait(true);
    }

    /// <summary>The active top-level's clipboard, or <c>null</c> before a window exists.</summary>
    private static IClipboard? GetActiveClipboard()
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow?.Clipboard;
        }

        return null;
    }
}
