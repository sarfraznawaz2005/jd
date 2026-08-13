using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Categorization;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Downloading;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Settings;
using JustDownload.Tests.Fakes;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// OS integration (TASK-061): completion/error notifications (AC0), the tray menu (AC1), and single-instance
/// argument forwarding (AC2). The pieces are tested through their seams; the live tray rendering and native
/// toast display are runtime concerns exercised when the app runs.
/// </summary>
public sealed class NotificationsTrayInstanceTests
{
    private sealed class RecordingNotifications : INotificationService
    {
        public List<AppNotification> Shown { get; } = [];

        public void Notify(AppNotification notification) => Shown.Add(notification);
    }


    private static IDownloadRepository RepoWithFilename(string filename)
    {
        var repo = Substitute.For<IDownloadRepository>();
        repo.GetAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Download?>(new Download { Url = "u", Status = "complete", Filename = filename }));
        return repo;
    }

    private static ISettingsService Settings(bool notificationsEnabled = true)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { NotificationsEnabled = notificationsEnabled });
        return settings;
    }

    // --- AC0: notifications ----------------------------------------------------------------------

    [Fact]
    public async Task Notifier_ShowsSuccess_OnComplete()
    {
        var manager = new FakeDownloadManager();
        var notifications = new RecordingNotifications();
        using var notifier = new DownloadNotifier(manager, RepoWithFilename("movie.mp4"), notifications, Settings());
        notifier.Start();

        manager.Raise(1, DownloadStatus.Completed);
        await Task.Delay(50);

        notifications.Shown.Should().ContainSingle();
        notifications.Shown[0].Kind.Should().Be(AppNotificationKind.Success);
        notifications.Shown[0].Message.Should().Contain("movie.mp4");
    }

    [Fact]
    public async Task Notifier_ShowsError_OnFailure()
    {
        var manager = new FakeDownloadManager();
        var notifications = new RecordingNotifications();
        using var notifier = new DownloadNotifier(manager, RepoWithFilename("iso.img"), notifications, Settings());
        notifier.Start();

        manager.Raise(2, DownloadStatus.Failed);
        await Task.Delay(50);

        notifications.Shown.Should().ContainSingle().Which.Kind.Should().Be(AppNotificationKind.Error);
    }

    [Fact]
    public async Task Notifier_Ignores_NonTerminalTransitions()
    {
        var manager = new FakeDownloadManager();
        var notifications = new RecordingNotifications();
        using var notifier = new DownloadNotifier(manager, RepoWithFilename("x"), notifications, Settings());
        notifier.Start();

        manager.Raise(1, DownloadStatus.Active);
        manager.Raise(1, DownloadStatus.Paused);
        await Task.Delay(50);

        notifications.Shown.Should().BeEmpty();
    }

    [Fact]
    public async Task Notifier_DoesNotNotify_WhenNotificationsDisabled()
    {
        var manager = new FakeDownloadManager();
        var notifications = new RecordingNotifications();
        using var notifier = new DownloadNotifier(
            manager, RepoWithFilename("x.bin"), notifications, Settings(notificationsEnabled: false));
        notifier.Start();

        manager.Raise(1, DownloadStatus.Completed);
        await Task.Delay(50);

        notifications.Shown.Should().BeEmpty("the notifications setting is off (TASK-123)");
    }

    // --- AC1: tray menu --------------------------------------------------------------------------

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void TrayMenu_HasCoreItems_ThatInvokeActions()
    {
        int show = 0, add = 0, quit = 0;
        NativeMenu menu = TrayMenuFactory.Create(() => show++, () => add++, () => quit++);

        var items = menu.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).ToList();
        items.Select(i => i.Header).Should().ContainInOrder("Show JustDownload", "New download…", "Quit");
        menu.Items.OfType<NativeMenuItemSeparator>().Should().ContainSingle("a separator precedes Quit");

        foreach (NativeMenuItem item in items)
        {
            item.Command.Should().NotBeNull();
            item.Command!.Execute(null);
        }

        (show, add, quit).Should().Be((1, 1, 1), "each tray item runs its action");
    }

    /// <summary>
    /// Regression for the tray "New Download" action wrongly bringing the main window to front: the New
    /// Download dialog is a fully independent top-level window (App.axaml.cs ShowIndependentAsync), so
    /// invoking it from the tray must not show or activate the main window, unlike the tray "Show" item.
    /// </summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void TrayMenu_NewDownload_DoesNotShowOrActivateMainWindow()
    {
        MainWindowViewModel mainViewModel = BuildMinimalMainViewModel();
        var window = new Window();
        var desktop = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
        var app = (JustDownload.App.App)Application.Current!;

        MethodInfo installTrayIcon = typeof(JustDownload.App.App)
            .GetMethod("InstallTrayIcon", BindingFlags.NonPublic | BindingFlags.Instance)!;
        installTrayIcon.Invoke(app, [desktop, window, mainViewModel]);

        int newDownloadRaised = 0;
        mainViewModel.NewDownloadRequested += (_, _) => newDownloadRaised++;

        NativeMenu menu = TrayIcon.GetIcons(app)!.Single().Menu!;
        NativeMenuItem newDownloadItem = menu.Items.OfType<NativeMenuItem>()
            .Single(i => Equals(i.Header, "New download…"));
        newDownloadItem.Command!.Execute(null);

        newDownloadRaised.Should().Be(1, "the tray item still triggers NewDownloadCommand");
        window.IsVisible.Should().BeFalse(
            "the New Download dialog is fully independent — the tray action must not show the main window");
    }

    private static MainWindowViewModel BuildMinimalMainViewModel()
    {
        var manager = Substitute.For<IDownloadManager>();
        var repository = Substitute.For<IDownloadRepository>();
        repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<JustDownload.Core.Data.Models.Download>>(
                Array.Empty<JustDownload.Core.Data.Models.Download>()));
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        var downloads = new DownloadsListViewModel(
            repository,
            manager,
            Substitute.For<IDownloadActions>(),
            Substitute.For<IClipboardService>(),
            Substitute.For<IFileRevealer>(),
            Substitute.For<IFileCategorizer>(),
            clock);
        var detail = new DownloadDetailViewModel(manager, Substitute.For<IDownloadActions>());
        var sidebar = new SidebarViewModel(downloads);

        return new MainWindowViewModel(
            new ThemeService(), Substitute.For<IDensityService>(), new StatusSummaryViewModel(manager),
            downloads, detail, sidebar);
    }

    // --- AC2: single-instance argument forwarding ------------------------------------------------

    [Fact]
    public async Task SecondInstance_ForwardsArguments_ToOwner()
    {
        string name = "JustDownload.Test." + Guid.NewGuid().ToString("N");
        using var owner = new SingleInstanceCoordinator(name);
        owner.IsOwnerShouldBeTrue();

        IReadOnlyList<string>? received = null;
        using var gate = new SemaphoreSlim(0, 1);
        owner.ArgumentsReceived += (_, args) => { received = args; gate.Release(); };

        using var second = new SingleInstanceCoordinator(name);
        second.TryClaimOwnership().Should().BeFalse("the owner already holds the single instance");

        await second.ForwardArgumentsAsync(["https://example.com/file.zip"]);
        (await gate.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue("the owner receives the forwarded args");

        received.Should().Equal("https://example.com/file.zip");
    }

    [Fact]
    public async Task NativeHostNotify_DeliversTheDrainInboxSignal_ToTheOwner()
    {
        // TASK-182: proves the actual contract AppLauncher.NotifyRunningInstanceAsync depends on -- that a
        // client resolving the pipe name through SingleInstancePipeName.Resolve(name) and sending
        // DrainInboxSignal is received, verbatim, by a SingleInstanceCoordinator owner listening on that
        // same name. AppLauncher's own production method is private and hardcodes the real app name, so
        // this replicates its exact client-side protocol against a test-scoped name instead of touching the
        // real production mutex/pipe (which could collide with an actual running app instance).
        string name = "JustDownload.Test." + Guid.NewGuid().ToString("N");
        using var owner = new SingleInstanceCoordinator(name);
        owner.IsOwnerShouldBeTrue();

        IReadOnlyList<string>? received = null;
        using var gate = new SemaphoreSlim(0, 1);
        owner.ArgumentsReceived += (_, args) => { received = args; gate.Release(); };

        await using (var client = new System.IO.Pipes.NamedPipeClientStream(
            ".", JustDownload.Core.NativeMessaging.SingleInstancePipeName.Resolve(name),
            System.IO.Pipes.PipeDirection.Out, System.IO.Pipes.PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000);
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(
                JustDownload.Core.NativeMessaging.SingleInstancePipeName.DrainInboxSignal);
            await client.WriteAsync(payload);
            await client.FlushAsync();
        }

        (await gate.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue("the owner receives the signal");
        received.Should().Equal(JustDownload.Core.NativeMessaging.SingleInstancePipeName.DrainInboxSignal);
    }
}

file static class CoordinatorTestExtensions
{
    /// <summary>Claims ownership and asserts this instance became the owner.</summary>
    public static void IsOwnerShouldBeTrue(this SingleInstanceCoordinator coordinator)
    {
        coordinator.TryClaimOwnership().Should().BeTrue("the first instance owns the single instance");
    }
}
