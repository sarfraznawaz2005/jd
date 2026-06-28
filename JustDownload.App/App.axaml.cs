using System.Linq;
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
            .AddSingleton<INotificationService, AvaloniaNotificationService>()
            .AddSingleton<DownloadNotifier>()
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

    /// <summary>The single-instance coordinator owned by this process (set by <c>Program</c>), if any.</summary>
    public static ISingleInstanceCoordinator? InstanceCoordinator { get; set; }

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

            // Notify on completion/error, add a tray icon, and accept URLs forwarded by a second launch (TASK-061).
            Services.GetRequiredService<DownloadNotifier>().Start();
            InstallTrayIcon(desktop, window, mainViewModel);
            WireForwardedArguments(window);

            // Bring the schema up to date, then load the persisted downloads into the list — off the
            // initialisation path so the window paints immediately and never blocks on I/O (§6).
            Dispatcher.UIThread.Post(async () => await InitializeAndLoadAsync(), DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InstallTrayIcon(
        IClassicDesktopStyleApplicationLifetime desktop, Window window, MainWindowViewModel mainViewModel)
    {
        NativeMenu menu = TrayMenuFactory.Create(
            show: () => BringToFront(window),
            newDownload: () => mainViewModel.NewDownloadCommand.Execute(null),
            quit: () => desktop.Shutdown());

        var tray = new TrayIcon
        {
            ToolTipText = "JustDownload",
            Icon = window.Icon is { } icon ? icon : null,
            Menu = menu,
        };
        tray.Clicked += (_, _) => BringToFront(window);

        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }

    private static void BringToFront(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private void WireForwardedArguments(Window window)
    {
        if (InstanceCoordinator is not { } coordinator)
        {
            return;
        }

        coordinator.ArgumentsReceived += (_, args) => Dispatcher.UIThread.Post(() =>
        {
            BringToFront(window);
            string? url = args.FirstOrDefault(a => Uri.TryCreate(a, UriKind.Absolute, out Uri? _));
            if (url is not null)
            {
                _ = ShowNewDownloadDialogAsync(window, url);
            }
        });
    }

    private async Task ShowNewDownloadDialogAsync(Window owner, string? prefillUrl = null)
    {
        var viewModel = Services.GetRequiredService<NewDownloadViewModel>();
        if (!string.IsNullOrWhiteSpace(prefillUrl))
        {
            viewModel.Url = prefillUrl;
        }

        var dialog = new NewDownloadWindow { DataContext = viewModel };
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
