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
using JustDownload.Core.Settings;
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

    /// <summary>
    /// Set once the app is genuinely quitting (tray "Quit", or the window closed with close-to-tray off) so
    /// the close-to-tray handler lets the close through instead of hiding the window again.
    /// </summary>
    private bool _isExplicitShutdown;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Capture and surface unhandled exceptions process-wide — no silent failures (§1).
        Services.GetRequiredService<IGlobalErrorHandler>().Install();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The tray means the app outlives its window: it may start hidden and keep running after the
            // window is closed to tray. We own the lifetime explicitly (tray "Quit" / window-close decide
            // when to exit) so hiding the last window never quits the process out from under us.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            MainWindowViewModel mainViewModel = Services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = mainViewModel };

            // The toolbar/command-palette "New URL" intent opens the dialog over the main window (TASK-052/053).
            mainViewModel.NewDownloadRequested += (_, _) => _ = ShowNewDownloadDialogAsync(window);

            // Detaching the per-download detail pops it into its own window (TASK-054 AC0).
            mainViewModel.Detail.DetachRequested += (_, _) => ShowDetachedDetail(window, mainViewModel.Detail);

            // The toolbar "Settings" intent opens the settings window (TASK-057).
            mainViewModel.SettingsRequested += (_, _) => _ = ShowSettingsDialogAsync(window);

            // A URL — dropped on the app (TASK-062) or forwarded by a second launch (TASK-061) — opens the
            // new-download dialog prefilled.
            mainViewModel.DownloadUrlRequested += (_, url) => _ = ShowNewDownloadDialogAsync(window, url);

            WireCloseToTray(desktop, window);

            // Notify on completion/error, add a tray icon, and accept URLs forwarded by a second launch (TASK-061).
            Services.GetRequiredService<DownloadNotifier>().Start();
            InstallTrayIcon(desktop, window, mainViewModel);
            WireForwardedArguments(window, mainViewModel);

            // Bring the schema up to date and load settings, then show the window (unless the user opted to
            // start hidden in the tray) and load the persisted downloads — off the startup path so we never
            // block, and so the window is shown only once we know whether it should be (no taskbar flash).
            Dispatcher.UIThread.Post(
                async () => await InitializeAndLoadAsync(desktop, window), DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Routes the title-bar close: when "close to tray" is on, a user close hides the window instead of
    /// quitting; otherwise closing the window quits the app (we run in <see cref="ShutdownMode.OnExplicitShutdown"/>).
    /// </summary>
    private void WireCloseToTray(IClassicDesktopStyleApplicationLifetime desktop, Window window)
    {
        window.Closing += (_, e) =>
        {
            if (_isExplicitShutdown)
            {
                return; // genuinely quitting — let the close proceed
            }

            if (Services.GetRequiredService<ISettingsService>().Current.CloseToTray)
            {
                e.Cancel = true;
                window.Hide();
            }
        };

        // A close we didn't cancel (close-to-tray off) means the user wants out — exit the app.
        window.Closed += (_, _) =>
        {
            if (!_isExplicitShutdown)
            {
                Quit(desktop);
            }
        };
    }

    private void Quit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _isExplicitShutdown = true;
        desktop.Shutdown();
    }

    private void InstallTrayIcon(
        IClassicDesktopStyleApplicationLifetime desktop, Window window, MainWindowViewModel mainViewModel)
    {
        NativeMenu menu = TrayMenuFactory.Create(
            show: () => BringToFront(window),
            newDownload: () =>
            {
                BringToFront(window); // ensure a visible owner for the dialog, even when started hidden
                mainViewModel.NewDownloadCommand.Execute(null);
            },
            quit: () => Quit(desktop));

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

    private static void WireForwardedArguments(Window window, MainWindowViewModel mainViewModel)
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
                mainViewModel.RequestDownloadForUrl(url);
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

    private async Task InitializeAndLoadAsync(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        bool shown = false;
        try
        {
            // Schema must be current before settings (and the rest of Core) read it.
            await Services.InitializeJustDownloadCoreAsync().ConfigureAwait(true);
            await Services.GetRequiredService<ISettingsService>().LoadAsync().ConfigureAwait(true);

            bool startMinimized = Services.GetRequiredService<ISettingsService>().Current.StartMinimizedToTray;
            ShowMainWindow(desktop, window, startMinimized);
            shown = true;

            await Services.GetRequiredService<DownloadsListViewModel>().LoadAsync().ConfigureAwait(true);
            await DeliverPendingLinksAsync().ConfigureAwait(true);
        }
        finally
        {
            // If initialisation failed before we decided visibility, still show the window so the app is
            // usable — the global error handler surfaces whatever went wrong.
            if (!shown)
            {
                ShowMainWindow(desktop, window, startMinimized: false);
            }
        }
    }

    /// <summary>
    /// Adopts the window as the main window and shows it, unless the user opted to start hidden in the tray.
    /// Done after the main loop is running so assigning <see cref="IClassicDesktopStyleApplicationLifetime.MainWindow"/>
    /// doesn't re-trigger the framework's startup auto-show.
    /// </summary>
    private static void ShowMainWindow(
        IClassicDesktopStyleApplicationLifetime desktop, Window window, bool startMinimized)
    {
        desktop.MainWindow = window;
        if (!startMinimized)
        {
            window.Show();
        }
    }

    /// <summary>Delivers links the extension handed off while the app was closed (TASK-070 AC1).</summary>
    private async Task DeliverPendingLinksAsync()
    {
        IReadOnlyList<JustDownload.Core.NativeMessaging.PendingLink> pending =
            await Services.GetRequiredService<JustDownload.Core.NativeMessaging.IExtensionInbox>()
                .DrainAsync().ConfigureAwait(true);
        if (pending.Count == 0)
        {
            return;
        }

        MainWindowViewModel mainViewModel = Services.GetRequiredService<MainWindowViewModel>();
        foreach (JustDownload.Core.NativeMessaging.PendingLink link in pending)
        {
            mainViewModel.RequestDownloadForUrl(link.Url);
        }
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
