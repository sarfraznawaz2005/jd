using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.Core.Lifecycle;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// Tests for the completion taskbar flash (TASK-226): only a completed download flashes, the shell's resolver
/// decides which window, and an unresolved (hidden) target is simply skipped.
/// </summary>
public sealed class TaskbarFlashNotifierTests
{
    private sealed class RecordingAttention : ITaskbarAttention
    {
        public RecordingAttention(bool supported = true) => IsSupported = supported;

        public bool IsSupported { get; }

        public List<Window> Flashed { get; } = [];

        public void Flash(Window window) => Flashed.Add(window);
    }

    private static void RaiseStatus(IDownloadManager manager, DownloadStatus status) =>
        manager.StatusChanged += Raise.Event<EventHandler<DownloadStatusChangedEventArgs>>(
            manager, new DownloadStatusChangedEventArgs(1, DownloadStatus.Active, status));

    [AvaloniaFact]
    public void FlashFor_UsesTheResolvedWindow()
    {
        var attention = new RecordingAttention();
        var target = new Window();
        using var notifier = new TaskbarFlashNotifier(
            Substitute.For<IDownloadManager>(), attention, _ => target);

        notifier.FlashFor(1);

        attention.Flashed.Should().ContainSingle().Which.Should().BeSameAs(target);
    }

    [AvaloniaFact]
    public void FlashFor_SkipsWhenNoWindowResolves()
    {
        var attention = new RecordingAttention();
        using var notifier = new TaskbarFlashNotifier(
            Substitute.For<IDownloadManager>(), attention, _ => null);

        notifier.FlashFor(1);

        attention.Flashed.Should().BeEmpty();
    }

    [AvaloniaFact]
    public void NonTerminalTransitions_DoNotFlash()
    {
        var manager = Substitute.For<IDownloadManager>();
        var attention = new RecordingAttention();
        using var notifier = new TaskbarFlashNotifier(manager, attention, _ => new Window());
        notifier.Start();

        RaiseStatus(manager, DownloadStatus.Paused);
        RaiseStatus(manager, DownloadStatus.Queued);
        Dispatcher.UIThread.RunJobs();

        attention.Flashed.Should().BeEmpty();
    }

    [AvaloniaTheory]
    [InlineData(DownloadStatus.Completed)]
    [InlineData(DownloadStatus.Failed)]
    public void TerminalTransitions_Flash(DownloadStatus terminal)
    {
        var manager = Substitute.For<IDownloadManager>();
        var attention = new RecordingAttention();
        var target = new Window();
        using var notifier = new TaskbarFlashNotifier(manager, attention, _ => target);
        notifier.Start();

        RaiseStatus(manager, terminal);
        Dispatcher.UIThread.RunJobs();

        attention.Flashed.Should().ContainSingle().Which.Should().BeSameAs(target);
    }

    [AvaloniaFact]
    public void UnsupportedPlatform_NeverFlashes()
    {
        var manager = Substitute.For<IDownloadManager>();
        var attention = new RecordingAttention(supported: false);
        using var notifier = new TaskbarFlashNotifier(manager, attention, _ => new Window());
        notifier.Start();

        RaiseStatus(manager, DownloadStatus.Completed);
        Dispatcher.UIThread.RunJobs();

        attention.Flashed.Should().BeEmpty();
    }

    [Fact]
    public void RealService_ReportsSupportForWindowsOnly() =>
        new TaskbarAttentionService().IsSupported.Should().Be(OperatingSystem.IsWindows());

    [AvaloniaFact]
    public void RealService_IsSafeOnAWindowWithNoPlatformHandle()
    {
        // Never shown, so TryGetPlatformHandle yields nothing — must be a quiet no-op, not a crash.
        Action flash = () => new TaskbarAttentionService().Flash(new Window());

        flash.Should().NotThrow();
    }
}
