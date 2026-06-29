using Avalonia.Controls;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Downloading;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Settings;
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

    private sealed class FakeManager : IDownloadManager
    {
        public event EventHandler<DownloadStatusChangedEventArgs>? StatusChanged;

#pragma warning disable CS0067
        public event EventHandler<DownloadProgressChangedEventArgs>? ProgressChanged;
#pragma warning restore CS0067

        public void Raise(long id, DownloadStatus current) =>
            StatusChanged?.Invoke(this, new DownloadStatusChangedEventArgs(id, DownloadStatus.Active, current));

        public Task<long> EnqueueAsync(EnqueueDownloadRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DownloadResult> StartAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DownloadResult> RenewAsync(long id, Uri u, CancellationToken ct = default) => throw new NotSupportedException();
        public DownloadProgress? GetProgress(long id) => null;
        public IReadOnlyList<ConnectionStat> GetConnections(long id) => [];
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
        var manager = new FakeManager();
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
        var manager = new FakeManager();
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
        var manager = new FakeManager();
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
        var manager = new FakeManager();
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
}

file static class CoordinatorTestExtensions
{
    /// <summary>Claims ownership and asserts this instance became the owner.</summary>
    public static void IsOwnerShouldBeTrue(this SingleInstanceCoordinator coordinator)
    {
        coordinator.TryClaimOwnership().Should().BeTrue("the first instance owns the single instance");
    }
}
