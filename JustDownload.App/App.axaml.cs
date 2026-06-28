using Avalonia;
using Avalonia.Controls;
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
            .AddSingleton<IDensityService, DensityService>()
            .AddSingleton<IDownloadActions, DownloadActionsService>()
            .AddSingleton<IFileRevealer, FileRevealer>()
            .AddSingleton<IDownloadFolderProvider, DownloadFolderProvider>()
            .AddSingleton<IClipboardService>(_ => new ClipboardService(GetActiveClipboard))
            .AddSingleton<StatusSummaryViewModel>()
            .AddSingleton<DownloadsListViewModel>()
            .AddSingleton<DownloadDetailViewModel>()
            .AddSingleton<SidebarViewModel>()
            .AddSingleton<MainWindowViewModel>()
            .AddTransient<NewDownloadViewModel>()
            .AddTransient<MediaVariantPickerViewModel>()
            .AddTransient<JustDownload.App.ViewModels.Settings.SettingsViewModel>()
            .BuildServiceProvider();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Capture and surface unhandled exceptions process-wide — no silent failures (§1).
        Services.GetRequiredService<IGlobalErrorHandler>().Install();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindowViewModel mainViewModel = Services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = mainViewModel };
            desktop.MainWindow = window;

            // The toolbar/command-palette "New URL" intent opens the dialog over the main window (TASK-052/053).
            mainViewModel.NewDownloadRequested += (_, _) => _ = ShowNewDownloadDialogAsync(window);

            // Detaching the per-download detail pops it into its own window (TASK-054 AC0).
            mainViewModel.Detail.DetachRequested += (_, _) => ShowDetachedDetail(window, mainViewModel.Detail);

            // The toolbar "Settings" intent opens the settings window (TASK-057).
            mainViewModel.SettingsRequested += (_, _) => _ = ShowSettingsDialogAsync(window);

            // Bring the schema up to date, then load the persisted downloads into the list — off the
            // initialisation path so the window paints immediately and never blocks on I/O (§6).
            Dispatcher.UIThread.Post(async () => await InitializeAndLoadAsync(), DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task ShowNewDownloadDialogAsync(Window owner)
    {
        var dialog = new NewDownloadWindow
        {
            DataContext = Services.GetRequiredService<NewDownloadViewModel>(),
        };

        await dialog.ShowDialog(owner);
    }

    private async Task ShowSettingsDialogAsync(Window owner)
    {
        var dialog = new SettingsWindow
        {
            DataContext = Services.GetRequiredService<JustDownload.App.ViewModels.Settings.SettingsViewModel>(),
        };

        await dialog.ShowDialog(owner);
    }

    private static void ShowDetachedDetail(Window owner, DownloadDetailViewModel detail)
    {
        // The detached window shares the same detail view-model as the inline pane, so both stay live and in
        // sync (AC0). Non-modal so the user can keep working in the main window.
        var window = new DownloadDetailWindow { DataContext = detail };
        window.Show(owner);
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
