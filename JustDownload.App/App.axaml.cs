using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.App.Views;
using JustDownload.Core;
using JustDownload.Core.Diagnostics;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.NativeMessaging;
using JustDownload.Core.Settings;
using JustDownload.Core.Throttling;
using JustDownload.Core.Transport.Proxy;
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
            .AddSingleton<AutoExtractService>()
            .AddSingleton<DownloadOrganizerService>()
            .AddSingleton<IProcessLauncher, ProcessLauncher>()
            .AddSingleton<PostDownloadCommandService>()
            .AddSingleton<IDownloadActions, DownloadActionsService>()
            .AddSingleton<IFileRevealer, FileRevealer>()
            .AddSingleton<IDownloadFolderProvider, DownloadFolderProvider>()
            .AddSingleton<IClipboardService>(_ => new ClipboardService(GetActiveClipboard))
            .AddSingleton<ClipboardMonitor>()
            .AddSingleton<ITosNoticeGate>(sp => new TosNoticeGate(sp.GetRequiredService<ISettingsService>(), _ => ShowTosNoticeAsync()))
            .AddSingleton<IAutostartService>(_ => CreateAutostartService())
            .AddSingleton<AutostartController>()
            .AddSingleton<LogLevelController>()
            .AddSingleton<StatusSummaryViewModel>()
            .AddSingleton<DownloadsListViewModel>()
            .AddSingleton<DownloadDetailViewModel>()
            .AddSingleton<SidebarViewModel>()
            .AddSingleton<MainWindowViewModel>()
            .AddTransient<NewDownloadViewModel>()
            .AddTransient<MediaVariantPickerViewModel>()
            .AddTransient<BrowsersViewModel>()
            .AddTransient<JustDownload.App.ViewModels.Settings.SettingsViewModel>()
            .BuildServiceProvider();

    /// <summary>The single-instance coordinator owned by this process (set by <c>Program</c>), if any.</summary>
    public static ISingleInstanceCoordinator? InstanceCoordinator { get; set; }

    /// <summary>
    /// Set once the app is genuinely quitting (tray "Quit", or the window closed with close-to-tray off) so
    /// the close-to-tray handler lets the close through instead of hiding the window again.
    /// </summary>
    private bool _isExplicitShutdown;

    /// <summary>Picks the per-OS launch-at-login backend (TASK-155). Falls back to the (inert,
    /// <c>IsSupported == false</c>) Windows implementation on an OS with no autostart mechanism.</summary>
    private static IAutostartService CreateAutostartService()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacOsAutostartService();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxAutostartService();
        }

        return new WindowsAutostartService();
    }

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

            // Begin sampling speed history for the bandwidth sparklines (TASK-137).
            mainViewModel.Status.Start();
            mainViewModel.Detail.Start();

            // The toolbar/command-palette "New URL" intent opens the dialog over the main window (TASK-052/053).
            mainViewModel.NewDownloadRequested += (_, _) => _ = ShowNewDownloadDialogAsync(window);

            // The toolbar "Settings" intent opens the settings window (TASK-057).
            mainViewModel.SettingsRequested += (_, _) => _ = ShowSettingsDialogAsync(window);

            // The add-media intent opens the quality picker, which enqueues the chosen variant (TASK-100).
            mainViewModel.NewMediaRequested += (_, _) => _ = ShowMediaPickerDialogAsync(window, mainViewModel);

            // Import a URL list / export the queue as M3U/CSV/JSON (TASK-140).
            mainViewModel.ImportListRequested += (_, _) => _ = ImportListAsync(window, mainViewModel);
            mainViewModel.ExportListRequested += (_, _) => _ = ExportListAsync(window);

            // A URL — dropped on the app (TASK-062) or forwarded by a second launch (TASK-061) — opens the
            // new-download dialog prefilled.
            mainViewModel.DownloadUrlRequested += (_, url) => _ = ShowNewDownloadDialogAsync(window, url);

            // A browser-extension hand-off (TASK-091) opens the dialog prefilled and carries the captured
            // referrer/cookies into the download so authenticated/signed links succeed.
            mainViewModel.DownloadHandoffRequested += (_, handoff) => _ = ShowNewDownloadDialogAsync(window, handoff);

            // Opt-in clipboard watcher (TASK-133): a copied supported URL opens the dialog prefilled.
            ClipboardMonitor clipboardMonitor = Services.GetRequiredService<ClipboardMonitor>();
            clipboardMonitor.UrlDetected += (_, url) => mainViewModel.RequestDownloadForUrl(url);

            // Remember the sidebar's expanded/collapsed state across restarts (the initial value is applied
            // in ApplyPersistedPreferences, alongside the theme, before the window paints).
            mainViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.SidebarCollapsed))
                {
                    _ = Services.GetRequiredService<ISettingsService>()
                        .UpdateAsync(s => s with { SidebarCollapsed = mainViewModel.SidebarCollapsed });
                }
            };

            WireCloseToTray(desktop, window);

            // Notify on completion/error, add a tray icon, and accept URLs forwarded by a second launch (TASK-061).
            Services.GetRequiredService<DownloadNotifier>().Start();
            Services.GetRequiredService<AutoExtractService>().Start();
            Services.GetRequiredService<DownloadOrganizerService>().Start();
            Services.GetRequiredService<PostDownloadCommandService>().Start();
            InstallTrayIcon(desktop, window, mainViewModel);
            WireForwardedArguments(mainViewModel);

            // Register the native-messaging host so browsers can find/launch it (TASK-089). Off-thread file/
            // registry writes; a no-op when the host executable isn't deployed alongside the app (dev).
            _ = Task.Run(() => Services.GetRequiredService<INativeHostInstaller>().Install());

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

        // Windows (and some Linux WMs) refuse a background process's plain Activate() call outright
        // (foreground-lock prevention) — the window merely flashes in the taskbar instead of coming
        // forward (user-reported: waking from the tray didn't reliably bring the window to focus).
        // Toggling Topmost is the standard, low-risk way to force it to the front regardless.
        window.Topmost = true;
        window.Activate();
        window.Topmost = false;
    }

    private void WireForwardedArguments(MainWindowViewModel mainViewModel)
    {
        if (InstanceCoordinator is not { } coordinator)
        {
            return;
        }

        coordinator.ArgumentsReceived += (_, args) => Dispatcher.UIThread.Post(() =>
        {
            // AppLauncher (a native-host client) sends this instead of a URL when a browser hand-off
            // arrives while this instance is already running (TASK-182) — re-drain the inbox now rather
            // than only at the next full restart, which is all that happened before this fix.
            if (args.Contains(JustDownload.Core.NativeMessaging.SingleInstancePipeName.DrainInboxSignal))
            {
                _ = DeliverPendingLinksAsync();
                return;
            }

            // Deliberately no BringToFront here: RequestDownloadForUrl opens the New Download dialog, which
            // ShowWithoutForcingOwnerVisibleAsync shows on its own when the main window is hidden/minimized to
            // tray, rather than forcing the whole window to the foreground for it.
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
        await ShowWithoutForcingOwnerVisibleAsync(owner, dialog);
    }

    private async Task ShowNewDownloadDialogAsync(Window owner, BrowserLinkHandoff handoff)
    {
        var viewModel = Services.GetRequiredService<NewDownloadViewModel>();
        viewModel.Url = handoff.Url;
        viewModel.SetAuthContext(handoff.Referrer, handoff.Cookies);

        var dialog = new NewDownloadWindow { DataContext = viewModel };
        await ShowWithoutForcingOwnerVisibleAsync(owner, dialog);
    }

    /// <summary>
    /// Shows the New Download dialog modally when <paramref name="owner"/> is already visible; otherwise (the
    /// main window is hidden/minimized to tray) opens it as an independent top-level window instead. A
    /// download arriving via the browser extension or a second launch while the user has tray'd the app must
    /// surface only this small confirmation dialog — forcing the whole main window visible just to satisfy
    /// <see cref="Window.ShowDialog"/>'s "visible owner" requirement was user-reported as unwanted: the main
    /// window should stay out of the way the user deliberately put it in.
    /// </summary>
    private static async Task ShowWithoutForcingOwnerVisibleAsync(Window owner, Window dialog)
    {
        if (owner.IsVisible)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }
    }

    private static readonly FilePickerFileType DownloadListFileType =
        new("Download lists") { Patterns = ["*.m3u", "*.m3u8", "*.csv", "*.json"] };

    private async Task ImportListAsync(Window owner, MainWindowViewModel mainViewModel)
    {
        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import URL list",
            AllowMultiple = false,
            FileTypeFilter = [DownloadListFileType],
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        string folder = Services.GetRequiredService<IDownloadFolderProvider>().GetBaseFolder();
        await Services.GetRequiredService<IDownloadListTransfer>().ImportAsync(path, folder);
        await mainViewModel.Downloads.LoadAsync(); // reflect the newly-queued downloads
    }

    private async Task ExportListAsync(Window owner)
    {
        IStorageFile? file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export queue",
            SuggestedFileName = "downloads.m3u",
            DefaultExtension = "m3u",
            FileTypeChoices = [DownloadListFileType],
        });

        if (file?.TryGetLocalPath() is { } path)
        {
            await Services.GetRequiredService<IDownloadListTransfer>().ExportAsync(path);
        }
    }

    private async Task ShowMediaPickerDialogAsync(Window owner, MainWindowViewModel mainViewModel)
    {
        var dialog = new MediaVariantPickerWindow
        {
            DataContext = Services.GetRequiredService<MediaVariantPickerViewModel>(),
        };
        object? enqueued = await dialog.ShowDialog<object?>(owner);
        if (enqueued is true)
        {
            await mainViewModel.Downloads.LoadAsync(); // reflect the newly-queued media download
        }
    }

    private async Task ShowSettingsDialogAsync(Window owner)
    {
        var dialog = new SettingsWindow
        {
            DataContext = Services.GetRequiredService<JustDownload.App.ViewModels.Settings.SettingsViewModel>(),
        };

        await dialog.ShowDialog(owner);
    }

    private async Task InitializeAndLoadAsync(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        bool shown = false;
        try
        {
            // Schema must be current before settings (and the rest of Core) read it.
            await Services.InitializeJustDownloadCoreAsync().ConfigureAwait(true);
            await Services.GetRequiredService<ISettingsService>().LoadAsync().ConfigureAwait(true);

            // Apply preferences that are only honored at startup, before the window paints (so a restart
            // opens in the saved theme with no flash) and so the global speed cap is enforced from the start.
            ApplyPersistedPreferences();

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
    /// Re-applies preferences that the rest of the app doesn't poll: the saved theme (which otherwise stays
    /// at the <see cref="ThemeService"/> default of Light on every launch) and the global speed limit (fed
    /// into the shared rate limiter, which is also kept in sync on later changes by the controller).
    /// </summary>
    private void ApplyPersistedPreferences()
    {
        AppSettings current = Services.GetRequiredService<ISettingsService>().Current;
        Services.GetRequiredService<IThemeService>()
            .SetMode(current.Theme == AppTheme.Dark ? ThemeMode.Dark : ThemeMode.Light);
        Services.GetRequiredService<MainWindowViewModel>().SidebarCollapsed = current.SidebarCollapsed;
        GlobalSpeedLimitController speedLimit = Services.GetRequiredService<GlobalSpeedLimitController>();
        speedLimit.ApplyCurrent();
        speedLimit.Start(); // re-evaluate the time-of-day schedule automatically (TASK-145)
        _ = Services.GetRequiredService<GlobalProxyController>().ApplyCurrentAsync();
        Services.GetRequiredService<ClipboardMonitor>().ApplyEnabled();
        Services.GetRequiredService<AutostartController>().ApplyCurrent();
        Services.GetRequiredService<LogLevelController>().ApplyCurrent();
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

    /// <summary>
    /// Delivers links the extension handed off while the app was closed (TASK-070 AC1) or while it was
    /// running but hidden to the tray (TASK-182, re-triggered live). Brings the main window to front first
    /// — before this fix, a hand-off arriving while minimized to tray opened the New Download dialog owned
    /// by a still-hidden window, so it never actually became visible (user-reported: downloads "did not
    /// work" while in the tray, for direct-click, the context menu, and video icons alike — all three share
    /// this same delivery path).
    /// </summary>
    private async Task DeliverPendingLinksAsync()
    {
        IReadOnlyList<JustDownload.Core.NativeMessaging.PendingLink> pending =
            await Services.GetRequiredService<JustDownload.Core.NativeMessaging.IExtensionInbox>()
                .DrainAsync().ConfigureAwait(true);
        if (pending.Count == 0)
        {
            return;
        }

        // The New Download dialog opens for each hand-off below regardless of whether the main window is
        // hidden/minimized to tray (ShowWithoutForcingOwnerVisibleAsync handles that) — the window itself is
        // deliberately left alone.
        MainWindowViewModel mainViewModel = Services.GetRequiredService<MainWindowViewModel>();
        foreach (JustDownload.Core.NativeMessaging.PendingLink link in pending)
        {
            // Carry the captured referrer/cookies through so authenticated/signed hand-offs succeed (TASK-091).
            mainViewModel.RequestDownloadHandoff(new BrowserLinkHandoff(link.Url, link.Referrer, link.Cookies));
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

    /// <summary>The app's main window, or <c>null</c> before it exists.</summary>
    private static Window? GetActiveWindow()
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }

        return null;
    }

    /// <summary>
    /// Shows the one-time ToS/legal notice (TASK-160) modally over the main window and returns the user's
    /// choice. Wired into <see cref="ITosNoticeGate"/> so <see cref="MediaVariantPickerViewModel"/> stays
    /// unaware of how the dialog is shown.
    /// </summary>
    private static Task<TosNoticeResult> ShowTosNoticeAsync()
    {
        var dialog = new TosNoticeWindow { DataContext = new TosNoticeViewModel() };
        Window owner = GetActiveWindow()
            ?? throw new InvalidOperationException("No active window to own the ToS notice dialog.");
        return dialog.ShowDialog<TosNoticeResult>(owner);
    }
}
